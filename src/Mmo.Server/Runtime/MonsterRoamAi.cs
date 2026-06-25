using Mmo.Shared.Domain;

namespace Mmo.Server.Runtime;

// LIVING-ENEMIES P1/P2: the server-side brain for EntityKind.Monster. One instance owns ALL monsters' per-entity
// AI state (keyed by entity id) plus the shared PRNG, and steps them on the server tick. It is deliberately
// decoupled from GameServer plumbing so it can be unit-tested directly against a WorldState + a walkability oracle
// + injected step/target/attack callbacks (no network, no sessions):
//
//   * Each monster has a HOME anchor (the tile it was spawned at), a roam RADIUS (Chebyshev tiles from home it
//     may wander within — the leash), and a small state machine:
//        Idle      — standing still until a pause timer elapses, then roam.
//        Roaming   — walking one tile per step cadence toward a chosen open destination within the leash.
//        Chasing   — (P2) a player entered aggro range; greedily step toward the TARGET's current tile and, when
//                    adjacent, attack on the monster's own attack cooldown. The chase MAY leave the roam radius
//                    (intended) but is LEASHED: target lost/dead/disconnected, target beyond the de-aggro range,
//                    or the monster farther than chaseLeash from home → drop aggro and Return.
//        Returning — (P2) walk greedily back toward home after dropping aggro; resume Idle on arrival.
//   * The monster moves through the SAME WorldEntity tile-step path players use (Zone.TryStep here, via the
//     injected stepper), so facing / StepSequence / AOI migration / replication / client interpolation all work
//     for free — there is NO parallel movement system.
//
// AGGRO THROTTLE (perf): the aggro scan (find the nearest player in range via the spatial index) is NOT run every
// tick per monster — the P1 review flagged a per-tick scan as a perf risk. Each monster re-evaluates aggro at most
// once every `aggroScanIntervalTicks` (~0.5 s), and the initial scan tick is staggered per entity id so a crowd of
// monsters doesn't all scan on the same tick.
//
// ATTACK is the monster's OWN per-monster timer (NextAttackTick), independent of its move cooldown — exactly like
// a player's attack cooldown is independent of its step cooldown. The damage application + the cosmetic damage
// number are an INJECTED callback (so combat stays in GameServer / the real ApplyDamage + DamageEventMessage path
// — the AI never forks combat).
//
// CORNER-CUT LIVELOCK FIX (P1 follow-up, todo/monster-roam-cornercut-livelock.md): a greedy step can wedge against
// a wall CORNER — the real step rejects a diagonal that cuts a corner, but the terrain oracle `_isWalkable(next)`
// is true, so the old "blocked?" heuristic mistook it for a cooldown wait and spun forever. A NO-PROGRESS TIMEOUT
// (`LastProgressTick`, set on entering a moving phase + on every successful step) bails when the monster has gone
// longer than ~2 step windows + a margin without advancing — catching the corner-cut (and any other block the
// terrain oracle misses) without re-implementing the corner-cut rule.
//
// DETERMINISM: the AI owns a seeded System.Random so tests are reproducible. The per-monster destination pick also
// folds in the entity id so two monsters seeded from the same instance don't roam in lockstep.
//
// NO death / respawn / loot this phase — that is Phase 3+. The player TAKES damage (HP floors at 0; it does not
// die or respawn here).
public sealed class MonsterRoamAi
{
    public enum State : byte
    {
        Idle = 0,
        Roaming = 1,
        Chasing = 2,
        Returning = 3,
    }

    // Per-monster AI record. Mutable struct held by value in the dictionary and written back on change — the state
    // set is tiny (a handful of monsters) so a dictionary keyed by entity id is the simplest store and costs nothing
    // measurable. Home is the leash centre; PauseUntilTick gates the next Idle→Roam transition; Destination is the
    // current roam/return target (meaningful while Roaming/Returning). TargetId/TargetPresent identify the chased
    // player (P2); NextAttackTick is the monster's OWN attack-cooldown gate; NextAggroScanTick throttles the aggro
    // scan; LastProgressTick is the corner-cut no-progress watchdog.
    private struct MonsterState
    {
        public TileCoord Home;
        public State Phase;
        public uint PauseUntilTick;
        public TileCoord Destination;

        // P2 aggro/chase/attack.
        public ulong TargetId;
        public bool TargetPresent;
        public uint NextAttackTick;
        public uint NextAggroScanTick;

        // P1 corner-cut fix: the last tick this monster actually advanced a tile (or entered a moving phase).
        public uint LastProgressTick;
    }

    private readonly Dictionary<ulong, MonsterState> _states = [];
    private readonly Random _random;

    // The walkability oracle (Zone.IsWalkable) and the single-tile stepper (Zone.TryStep). Injected so the AI is
    // testable against a bare TileGrid/WorldState without a live Zone/GameServer. The stepper returns whether the
    // tile actually advanced; the AI uses that to detect "blocked / made no progress" and bail.
    private readonly Func<TileCoord, bool> _isWalkable;
    private readonly TryStepDelegate _tryStep;

    // P2: find the nearest VALID aggro target (alive player) within `aggroRadius` of `monster`. Returns false when
    // none. Injected so the AI doesn't reach into WorldState/sessions directly — GameServer supplies the real
    // spatial-index scan (GatherInterestCandidates, players only, alive), keeping the AI unit-testable with a fake.
    private readonly FindTargetDelegate _findTarget;

    // P2: resolve a tracked target id to its live entity. Returns false if the target is gone (despawned/logged
    // out). Lets the AI re-read the target's CURRENT tile each chase step and detect target-lost for de-aggro.
    private readonly TryResolveTargetDelegate _tryResolveTarget;

    // P2: perform the monster's attack against `target` (face, ApplyDamage(attackDamage), emit the cosmetic damage
    // number for the player + nearby viewers). Injected so the real combat/replication path stays in GameServer and
    // the AI never forks combat. Returns nothing — the AI only owns the cooldown gate around it.
    private readonly AttackDelegate _attack;

    // The stepper signature mirrors Zone.TryStep (entity, direction, serverTick, stepCooldownTicks) → accepted.
    public delegate bool TryStepDelegate(WorldEntity entity, Direction8 direction, uint serverTick, uint stepCooldownTicks);

    // Finds the nearest alive player within `aggroRadius` of `monster`. On success returns true and outputs the
    // target id + its current tile; false when no eligible target is in range.
    public delegate bool FindTargetDelegate(WorldEntity monster, int aggroRadius, out ulong targetId, out TileCoord targetTile);

    // Resolves a tracked target id to its live entity + current tile + alive flag. False if the entity no longer
    // exists at all.
    public delegate bool TryResolveTargetDelegate(ulong targetId, out TileCoord targetTile, out bool alive);

    // Applies the monster's melee attack to the target id (face + damage + damage-number broadcast). Owned by
    // GameServer; the AI only decides WHEN (adjacency + cooldown).
    public delegate void AttackDelegate(WorldEntity monster, ulong targetId, int attackDamage);

    public MonsterRoamAi(
        int seed,
        Func<TileCoord, bool> isWalkable,
        TryStepDelegate tryStep,
        FindTargetDelegate findTarget,
        TryResolveTargetDelegate tryResolveTarget,
        AttackDelegate attack)
    {
        _random = new Random(seed);
        _isWalkable = isWalkable;
        _tryStep = tryStep;
        _findTarget = findTarget;
        _tryResolveTarget = tryResolveTarget;
        _attack = attack;
    }

    public int TrackedCount => _states.Count;

    // Tunables the per-tick step reads. Grouped in a struct so the (growing) parameter list stays readable and the
    // caller fills it from the live ServerTuning each tick. All values are already tick-quantised by ServerTuning.
    public readonly record struct Tunables(
        int RoamRadius,
        uint PauseMinTicks,
        uint PauseMaxTicks,
        int AggroRadius,
        int DeaggroRadius,
        int ChaseLeash,
        int AttackRange,
        int AttackDamage,
        uint AttackCooldownTicks,
        uint AggroScanIntervalTicks);

    // Registers a freshly spawned monster: records its spawn tile as the leash home and starts it Idle with an
    // initial randomized pause so it doesn't all-at-once lurch on the first eligible tick. The first aggro scan is
    // staggered by entity id (mod the scan interval) so a crowd of monsters doesn't scan on the same tick.
    public void Register(WorldEntity monster, uint serverTick, uint pauseMinTicks, uint pauseMaxTicks, uint aggroScanIntervalTicks)
    {
        var stagger = aggroScanIntervalTicks == 0 ? 0u : (uint)(monster.Id % aggroScanIntervalTicks);
        _states[monster.Id] = new MonsterState
        {
            Home = monster.TileCoord,
            Phase = State.Idle,
            PauseUntilTick = serverTick + NextPauseTicks(pauseMinTicks, pauseMaxTicks),
            Destination = monster.TileCoord,
            TargetId = 0,
            TargetPresent = false,
            NextAttackTick = serverTick,
            NextAggroScanTick = serverTick + stagger,
            LastProgressTick = serverTick,
        };
    }

    // Drops a monster's AI state (e.g. on despawn). No-op if untracked.
    public void Forget(ulong monsterId) => _states.Remove(monsterId);

    // Returns the current AI phase for a monster (test/diagnostic visibility). False if untracked.
    public bool TryGetPhase(ulong monsterId, out State phase)
    {
        if (_states.TryGetValue(monsterId, out var s))
        {
            phase = s.Phase;
            return true;
        }

        phase = State.Idle;
        return false;
    }

    // Returns the leash home anchor for a monster (test/diagnostic visibility). False if untracked.
    public bool TryGetHome(ulong monsterId, out TileCoord home)
    {
        if (_states.TryGetValue(monsterId, out var s))
        {
            home = s.Home;
            return true;
        }

        home = default;
        return false;
    }

    // Returns the chased target id while Chasing (test/diagnostic visibility). False if untracked or not chasing.
    public bool TryGetTarget(ulong monsterId, out ulong targetId)
    {
        if (_states.TryGetValue(monsterId, out var s) && s.Phase == State.Chasing && s.TargetPresent)
        {
            targetId = s.TargetId;
            return true;
        }

        targetId = 0;
        return false;
    }

    // Steps ONE tracked monster for this tick. Paced by the caller exactly like StepHeldMovementIntents: the caller
    // invokes this each tick with the monster's effective step-cooldown ticks; the underlying TryStep gate drops a
    // too-early step on cooldown, so the monster physically advances at most one tile per cooldown. The pause /
    // attack / aggro-scan timers are measured in ticks too, so the whole behaviour is tick-quantised.
    //
    //   AGGRO (all non-Chasing phases): throttled to NextAggroScanTick — scan for the nearest player in aggroRadius;
    //            on a hit, switch to Chasing that target (overriding Idle/Roaming/Returning).
    //   Idle:    do nothing until PauseUntilTick. Then pick a random OPEN roam tile within the leash → Roaming.
    //   Roaming: greedy step toward the destination; on arrival / a true block / the no-progress timeout → Idle.
    //   Chasing: re-read the target's CURRENT tile; check the leash (target lost/dead, beyond de-aggro range, or
    //            monster beyond chaseLeash from home) → Returning. If adjacent (Chebyshev <= attackRange) and the
    //            attack cooldown elapsed → attack (face + damage + number) on the OWN attack timer; else greedy step
    //            toward the target (with the no-progress watchdog).
    //   Returning: greedy step toward home; on arrival / a true block / the no-progress timeout → Idle (re-pause).
    //
    // Returns true iff the monster's tile actually advanced this call (so the caller can mark replication / trace).
    public bool StepMonster(WorldEntity monster, uint serverTick, uint stepCooldownTicks, in Tunables t)
    {
        if (!_states.TryGetValue(monster.Id, out var state))
        {
            return false;
        }

        // AGGRO (throttled): scan for a player in range and switch to Chasing on a hit — ONLY from Idle/Roaming.
        // NOT while Chasing (already has a target), and NOT while Returning: a leashed/evading monster commits to
        // reaching home before it re-evaluates aggro. Otherwise it re-aggros an in-range kiter mid-return and
        // vibrates at the leash edge forever instead of a clean out-and-back (it would never reach home + settle).
        // Throttled per monster (NextAggroScanTick), the SINGLE spatial-scan site, staggered at Register — the perf
        // throttle the P1 review asked for. The throttle advances whether or not a target was found.
        if ((state.Phase is State.Idle or State.Roaming) && serverTick >= state.NextAggroScanTick)
        {
            if (t.AggroRadius > 0 && _findTarget(monster, t.AggroRadius, out var foundId, out _))
            {
                EnterChase(ref state, foundId, serverTick);
            }

            // Advance the throttle off the SCAN tick (always), so the next scan is a full interval out regardless of
            // the outcome. If we just entered Chasing, this still records when the next scan would be eligible — moot
            // while Chasing, refreshed to "now" by BeginReturnHome when the chase ends.
            state.NextAggroScanTick = serverTick + Math.Max(1u, t.AggroScanIntervalTicks);
        }

        var moved = false;

        switch (state.Phase)
        {
            case State.Idle:
                if (serverTick >= state.PauseUntilTick)
                {
                    if (TryPickRoamDestination(monster, state.Home, t.RoamRadius, out var destination))
                    {
                        state.Destination = destination;
                        state.Phase = State.Roaming;
                        state.LastProgressTick = serverTick;
                        // Fall through to take the first roam step THIS tick (the pause already elapsed).
                        moved = StepTowardDestination(monster, ref state, serverTick, stepCooldownTicks, t, returningHome: false);
                    }
                    else
                    {
                        // Boxed in — no open tile in the leash. Stay Idle, re-pause so we re-test later.
                        state.PauseUntilTick = serverTick + NextPauseTicks(t.PauseMinTicks, t.PauseMaxTicks);
                    }
                }

                break;

            case State.Roaming:
                moved = StepTowardDestination(monster, ref state, serverTick, stepCooldownTicks, t, returningHome: false);
                break;

            case State.Chasing:
                moved = StepChase(monster, ref state, serverTick, stepCooldownTicks, t);
                break;

            case State.Returning:
                moved = StepTowardDestination(monster, ref state, serverTick, stepCooldownTicks, t, returningHome: true);
                break;
        }

        _states[monster.Id] = state;
        return moved;
    }

    // P2: enters Chasing a target. The chase destination is re-read live each step from the target's CURRENT tile,
    // so Destination here is just seeded; LastProgressTick starts the no-progress watchdog. The attack cooldown is
    // NOT reset on entering chase (so a monster that just attacked, lost LOS, and re-aggroed can't instantly re-hit;
    // its own timer still governs) unless it has never attacked — Register seeds NextAttackTick = spawn tick so the
    // first adjacency hits immediately.
    private void EnterChase(ref MonsterState state, ulong targetId, uint serverTick)
    {
        state.Phase = State.Chasing;
        state.TargetId = targetId;
        state.TargetPresent = true;
        state.LastProgressTick = serverTick;
    }

    // P2: one chase tick. (1) Resolve the target and apply the leash rules → Returning on any break. (2) If adjacent
    // within attackRange and the attack cooldown elapsed, attack on the OWN attack timer (no move this tick). (3)
    // Otherwise greedy-step toward the target's current tile, with the corner-cut no-progress watchdog.
    private bool StepChase(WorldEntity monster, ref MonsterState state, uint serverTick, uint stepCooldownTicks, in Tunables t)
    {
        // (1) De-aggro checks. Target gone/dead, OR beyond the de-aggro range, OR the monster has been pulled
        // farther than chaseLeash from home → drop aggro and walk home.
        if (!_tryResolveTarget(state.TargetId, out var targetTile, out var alive) || !alive)
        {
            BeginReturnHome(ref state, serverTick);
            return false;
        }

        var distToTarget = Chebyshev(monster.TileCoord, targetTile);
        var distFromHome = Chebyshev(monster.TileCoord, state.Home);
        if (distToTarget > t.DeaggroRadius || distFromHome > t.ChaseLeash)
        {
            BeginReturnHome(ref state, serverTick);
            return false;
        }

        // (2) Adjacent + off cooldown → attack (face + damage + number happen in the injected callback). The attack
        // is the monster's OWN per-monster timer, independent of its move cooldown. We do NOT move on an attack tick.
        if (distToTarget <= t.AttackRange)
        {
            // Face the target while adjacent (an attack tick takes no step, so nothing else sets facing). The
            // injected attack callback also faces the victim (same sign-of-delta, harmless redundancy) — facing
            // here keeps it in the testable AI path, not only the GameServer callback.
            monster.TrySetFacing(GreedyDirectionToward(monster.TileCoord, targetTile));
            if (serverTick >= state.NextAttackTick)
            {
                _attack(monster, state.TargetId, t.AttackDamage);
                state.NextAttackTick = serverTick + t.AttackCooldownTicks;
                state.LastProgressTick = serverTick; // adjacency-and-attacking counts as progress (not wedged).
            }

            return false;
        }

        // (3) Greedy step toward the target's current tile. The same stepper players use (facing set on the step).
        var direction = GreedyDirectionToward(monster.TileCoord, targetTile);
        var stepped = _tryStep(monster, direction, serverTick, stepCooldownTicks);
        if (stepped)
        {
            state.LastProgressTick = serverTick;
            return true;
        }

        // Not stepped: a cooldown wait leaves us here harmlessly (re-try next tick). But a true block / corner-cut
        // wedge would spin forever — the no-progress watchdog bails to Returning so a monster can never freeze
        // mid-chase against a wall corner (it walks home and re-roams; it can re-aggro on the next scan).
        if (NoProgressTimedOut(state.LastProgressTick, serverTick, stepCooldownTicks))
        {
            BeginReturnHome(ref state, serverTick);
        }

        return false;
    }

    // P2: drop aggro and head home. Destination = home; Returning greedily walks there and resumes Idle on arrival.
    private void BeginReturnHome(ref MonsterState state, uint serverTick)
    {
        state.Phase = State.Returning;
        state.TargetPresent = false;
        state.Destination = state.Home;
        state.LastProgressTick = serverTick;
        // Re-scan for aggro promptly once we start returning (don't wait a full interval) so a player who steps back
        // into range mid-return re-aggros quickly; staggered scan still applies on subsequent ticks.
        state.NextAggroScanTick = serverTick;
    }

    // One greedy tile-step toward the destination (roam target or, when returningHome, the home anchor). Reduces
    // whichever axis distance is larger (allowing diagonals when both axes still differ), maps that (dx,dy) to a
    // Direction8, and routes it through the SAME stepper players use. On arrival, on a true terrain block, OR on the
    // no-progress watchdog (corner-cut livelock), flip back to Idle with a fresh pause. The cooldown gate inside
    // TryStep makes a too-early call a harmless no-op that simply re-tries next tick — that does NOT end the move.
    private bool StepTowardDestination(
        WorldEntity monster,
        ref MonsterState state,
        uint serverTick,
        uint stepCooldownTicks,
        in Tunables t,
        bool returningHome)
    {
        if (monster.TileCoord == state.Destination)
        {
            GoIdle(ref state, serverTick, t.PauseMinTicks, t.PauseMaxTicks);
            return false;
        }

        var direction = GreedyDirectionToward(monster.TileCoord, state.Destination);
        var before = monster.TileCoord;
        var stepped = _tryStep(monster, direction, serverTick, stepCooldownTicks);

        if (stepped)
        {
            state.LastProgressTick = serverTick;
            // Advanced one tile. If that landed us on the destination, go Idle; otherwise keep moving.
            if (monster.TileCoord == state.Destination)
            {
                GoIdle(ref state, serverTick, t.PauseMinTicks, t.PauseMaxTicks);
            }

            return true;
        }

        // Not stepped. Distinguish "cooldown — just wait" from "blocked — give up". A true terrain block (the next
        // tile is unwalkable) bails immediately. The corner-cut case is the SUBTLE one (P1 follow-up): _tryStep
        // rejects the diagonal but _isWalkable(nextTile) is TRUE, so the terrain check below would say "wait" and
        // the AI would spin forever. The no-progress watchdog catches it: if the monster has gone longer than ~2
        // step windows without advancing, it can't be a mere cooldown wait → bail.
        var delta = direction.Delta();
        var nextTile = before.Offset(delta.X, delta.Y);
        if (!_isWalkable(nextTile) || NoProgressTimedOut(state.LastProgressTick, serverTick, stepCooldownTicks))
        {
            GoIdle(ref state, serverTick, t.PauseMinTicks, t.PauseMaxTicks);
        }

        return false;
    }

    // P1 corner-cut fix: true once the monster has gone longer than ~2 step windows + a margin without advancing —
    // longer than any legitimate cooldown wait can explain, so a wedge (corner-cut diagonal or any block the terrain
    // oracle misses) is detected and the caller bails instead of spinning. Margin (+1) absorbs tick-rounding so a
    // monster on a normal cadence is never falsely flagged.
    private static bool NoProgressTimedOut(uint lastProgressTick, uint serverTick, uint stepCooldownTicks)
    {
        return serverTick > lastProgressTick && (serverTick - lastProgressTick) > stepCooldownTicks * 2 + 1;
    }

    private void GoIdle(ref MonsterState state, uint serverTick, uint pauseMinTicks, uint pauseMaxTicks)
    {
        state.Phase = State.Idle;
        state.TargetPresent = false;
        state.PauseUntilTick = serverTick + NextPauseTicks(pauseMinTicks, pauseMaxTicks);
    }

    // Picks a random OPEN tile within Chebyshev `roamRadius` of home (the leash), excluding the monster's current
    // tile (so a roam is always a real move). Tries a bounded number of random offsets, then falls back to a
    // deterministic scan of the leash box so a sparse-but-non-empty leash still yields a target; returns false only
    // when the entire leash box (minus the current tile) is unwalkable. The chosen tile being within roamRadius of
    // home is what KEEPS the monster leashed during roam: a greedy walk toward an in-radius tile never leaves it.
    private bool TryPickRoamDestination(WorldEntity monster, TileCoord home, int roamRadius, out TileCoord destination)
    {
        destination = monster.TileCoord;
        if (roamRadius <= 0)
        {
            return false;
        }

        const int probes = 12;
        for (var i = 0; i < probes; i++)
        {
            var candidate = home.Offset(
                _random.Next(-roamRadius, roamRadius + 1),
                _random.Next(-roamRadius, roamRadius + 1));
            if (candidate != monster.TileCoord && _isWalkable(candidate))
            {
                destination = candidate;
                return true;
            }
        }

        var span = roamRadius * 2 + 1;
        var cellCount = span * span;
        var start = (int)((uint)(_random.Next() ^ (int)monster.Id) % (uint)cellCount);
        for (var k = 0; k < cellCount; k++)
        {
            var index = (start + k) % cellCount;
            var dx = (index % span) - roamRadius;
            var dy = (index / span) - roamRadius;
            var candidate = home.Offset(dx, dy);
            if (candidate != monster.TileCoord && _isWalkable(candidate))
            {
                destination = candidate;
                return true;
            }
        }

        return false;
    }

    // Greedy one-tile direction from `from` toward `to`: sign of each axis delta, allowing a diagonal when both axes
    // still differ. Reaches the destination in max(|dx|,|dy|) steps (the Chebyshev distance) — the natural
    // 8-direction walk. `from == to` is never passed here (callers check arrival/adjacency first), defaults to S.
    private static Direction8 GreedyDirectionToward(TileCoord from, TileCoord to)
    {
        var sx = Math.Sign(to.X - from.X);
        var sy = Math.Sign(to.Y - from.Y);
        return (sx, sy) switch
        {
            (0, -1) => Direction8.N,
            (1, -1) => Direction8.NE,
            (1, 0) => Direction8.E,
            (1, 1) => Direction8.SE,
            (0, 1) => Direction8.S,
            (-1, 1) => Direction8.SW,
            (-1, 0) => Direction8.W,
            (-1, -1) => Direction8.NW,
            _ => Direction8.S,
        };
    }

    // Chebyshev (8-direction) tile distance — the number of greedy diagonal-allowed steps between two tiles, the
    // same metric the leash / adjacency / aggro range all use.
    private static int Chebyshev(TileCoord a, TileCoord b)
        => Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    // A random pause length in [min, max] ticks (inclusive). Floored at 1 so a degenerate min/max can never produce
    // a zero-pause loop that would step every tick.
    private uint NextPauseTicks(uint pauseMinTicks, uint pauseMaxTicks)
    {
        var lo = Math.Max(1u, pauseMinTicks);
        var hi = Math.Max(lo, pauseMaxTicks);
        return (uint)_random.Next((int)lo, (int)hi + 1);
    }
}
