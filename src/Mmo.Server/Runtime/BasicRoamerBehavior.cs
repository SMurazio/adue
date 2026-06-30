using Mmo.Shared.Domain;

namespace Mmo.Server.Runtime;

// LIVING-ENEMIES P1/P2 + CONTINUOUS MIGRATION (Phase 8): the server-side brain for EntityKind.Monster. One instance
// owns ALL monsters' per-entity AI state (keyed by entity id) plus the shared PRNG, and steps them on the server tick.
// It is deliberately decoupled from GameServer plumbing so it can be unit-tested directly against a WorldState + a
// walkability oracle + a per-step locomotion + target/attack callbacks (no network, no sessions). MONSTER-BEHAVIOR P1:
// the locomotion is now passed PER TICK to StepMonster (resolved per-TYPE by GameServer), not injected once — the AI is
// locomotion-agnostic, so a per-type body (P2 GlideLocomotion) needs no change here:
//
//   * Each monster has a HOME anchor (the continuous point it was spawned at), a roam RADIUS (Euclidean world units
//     from home it may wander within — the leash), and a small state machine:
//        Idle      — standing still until a pause timer elapses, then roam.
//        Roaming   — HOPPING toward a chosen open destination within the leash (one collision-valid leap per cadence).
//        Chasing   — a player entered aggro range; hop toward the TARGET's current Position and, when within
//                    AttackRangeUnits, attack on the monster's own attack cooldown. The chase MAY leave the roam
//                    radius (intended) but is LEASHED: target lost/dead/disconnected, target beyond the de-aggro
//                    range, or the monster farther than chaseLeash from home → drop aggro and Return.
//        Returning — hop back toward home after dropping aggro; resume Idle on arrival.
//
// CONTINUOUS NAVIGATION (Phase 8). Navigation is CONTINUOUS (Euclidean ranges on WorldVector Position, sub-tile
// targets, obstacle-avoidance via the swept-circle resolver) but movement still HOPS — a discrete collision-valid
// leap per move cadence with Velocity left at Zero (the sparse-update "jump" is preserved; monsters stay OFF the
// player velocity-glide path). The hop primitive is the per-step IMonsterLocomotion (ship HopLocomotion); the AI owns
// only WHERE (the continuous target) and WHEN (the state machine + timers + the livelock watchdog).
//
// RANGE CONVERSION TABLE (Chebyshev tiles → Euclidean world units; behaviour-preserving, auditable):
//   | Range            | old (Chebyshev) | Euclidean | note                                                         |
//   |------------------|-----------------|-----------|--------------------------------------------------------------|
//   | Aggro            | 6               | 6.0       | cardinal-preserving; diagonal corners trim to a TRUE circle  |
//   | De-aggro         | ⌈1.5·aggro⌉ = 9 | 9.0       | keep the ⌈1.5·aggro⌉ hysteresis rule on the float            |
//   | Chase leash      | 12              | 12.0      | soft bound (one-hop overshoot allowed)                       |
//   | Roam             | 4               | 4.0       | roam destination sampled in the Euclidean disc               |
//   | Attack/adjacency | 1 (3×3)         | 1.5       | √2-covering so the diagonal still hits (1.0 would REGRESS)   |
// The aggro PRE-FILTER (FindTargetDelegate) still gathers by a tile/Chebyshev radius for the coarse spatial scan, but
// the caller passes ⌈AggroRadius⌉(+1) so no in-range Euclidean target is dropped; the AI then Euclidean-tests Position.
//
// AGGRO THROTTLE (perf): the aggro scan (find the nearest player in range via the spatial index) is NOT run every
// tick per monster — the P1 review flagged a per-tick scan as a perf risk. Each monster re-evaluates aggro at most
// once every `aggroScanIntervalTicks` (~0.5 s), and the initial scan tick is staggered per entity id so a crowd of
// monsters doesn't all scan on the same tick.
//
// ATTACK is the monster's OWN per-monster timer (NextAttackTick), independent of its move cadence — exactly like a
// player's attack cooldown is independent of its step cooldown. The damage application + the cosmetic damage number
// are an INJECTED callback (so combat stays in GameServer / the real ApplyDamage + DamageEventMessage path — the AI
// never forks combat).
//
// LIVELOCK WATCHDOG (Phase 8 re-base of the P1 corner-cut fix): a hop can wedge against a wall the resolver slides
// along to a FIXPOINT (a perpendicular wall, an inside corner the fan can't escape). "Progress" is now a resolved
// landing that advanced >= an epsilon (HopLocomotion.ProgressEpsilonUnits) toward the target — reported as
// HopResult.Moved. CRITICAL: a hop that the locomotion ATTEMPTED (cadence elapsed) but that landed within epsilon of
// `from` returns HopResult.Stuck and counts as NO-progress (NOT a cooldown wait), so the no-progress timeout always
// eventually fires at a slide fixpoint: Chasing → BeginReturnHome, Roaming/Returning → GoIdle + re-pick. An
// OnCooldown tick (the cadence simply has not elapsed) is a harmless wait and does NOT reset OR trip the watchdog.
//
// DETERMINISM: the AI owns a seeded System.Random so tests are reproducible. The per-monster destination pick folds
// in the entity id so two monsters seeded from the same instance don't roam in lockstep; the hop locomotion + the
// resolver are RNG-free and all-double, so a given seed replays an identical path.
//
// NO death / respawn / loot are this file's concern — those live in GameServer. The player TAKES damage via the
// injected attack callback.
//
// MONSTER-BEHAVIOR P3 (docs/monster-behavior-design.md): this IS the seam's first IMonsterBehavior — the slime/gnoll
// brain (formerly the standalone MonsterRoamAi). GameServer selects it per type via MonsterType.BehaviorId. NO behavior
// change at P3: only "basicRoamer" is registered, so every type resolves here → identical AI. Its per-tick INPUT
// (formerly the nested Tunables) is now the top-level MonsterAiTunables (MonsterBehavior.cs), shared by all behaviors.
// MONSTER-BEHAVIOR P4 (docs/monster-behavior-design.md): NO LONGER sealed — this is the shared brain SCAFFOLDING a
// second behavior composes by overriding only the DECISIONS that differ (the design's "base-class-with-overrides"
// resolution of the open P3 question). SkirmisherBehavior : BasicRoamerBehavior overrides exactly ONE hook
// (TryChooseFleeTarget) to FLEE when wounded and otherwise inherits the entire Idle/Roaming/Chasing/Returning state
// machine unchanged. The hook defaults to "never flee", so BasicRoamer (the slime) is byte-identical.
public class BasicRoamerBehavior : IMonsterBehavior
{
    public enum State : byte
    {
        Idle = 0,
        Roaming = 1,
        Chasing = 2,
        Returning = 3,
    }

    // Per-monster AI record. Mutable struct held by value in the dictionary and written back on change. Home is the
    // leash centre (continuous); PauseUntilTick gates the next Idle→Roam transition; Destination is the current
    // roam/return target (continuous; meaningful while Roaming/Returning). TargetId/TargetPresent identify the chased
    // player; NextAttackTick is the monster's OWN attack-cooldown gate; NextAggroScanTick throttles the aggro scan;
    // LastProgressTick is the livelock no-progress watchdog.
    private struct MonsterState
    {
        public WorldVector Home;
        public State Phase;
        public uint PauseUntilTick;
        public WorldVector Destination;

        // Aggro/chase/attack.
        public ulong TargetId;
        public bool TargetPresent;
        public uint NextAttackTick;
        public uint NextAggroScanTick;

        // The last tick this monster actually advanced (HopResult.Moved) or entered a moving phase — the watchdog base.
        public uint LastProgressTick;
    }

    private readonly Dictionary<ulong, MonsterState> _states = [];
    private readonly Random _random;

    // The walkability oracle (Zone.IsWalkable) — used to validate a sampled roam destination tile. Injected so the AI
    // is testable against a bare TileGrid/WorldState without a live Zone/GameServer.
    private readonly Func<TileCoord, bool> _isWalkable;

    // Find the nearest VALID aggro target (alive player) within `gatherRadius` (a coarse Chebyshev/tile pre-filter) of
    // `monster`. Returns false when none. Injected so the AI doesn't reach into WorldState/sessions directly —
    // GameServer supplies the real spatial-index scan (GatherInterestCandidates, players only, alive) and outputs the
    // target's CONTINUOUS Position; the AI does the precise Euclidean range test.
    private readonly FindTargetDelegate _findTarget;

    // Resolve a tracked target id to its live entity. Returns false if the target is gone (despawned/logged out). Lets
    // the AI re-read the target's CURRENT Position each chase hop and detect target-lost for de-aggro.
    private readonly TryResolveTargetDelegate _tryResolveTarget;

    // Perform the monster's attack against `target` (face, ApplyDamage, emit the damage number). Injected so the real
    // combat/replication path stays in GameServer and the AI never forks combat. The AI only owns the cooldown gate.
    private readonly AttackDelegate _attack;

    // MONSTER-BEHAVIOR P5: the charge-ability trigger (default a no-op that returns false → never charge). See
    // TryChargeDelegate. Resolved to a non-null delegate in the ctor so the StepChase trigger can call it unguarded.
    private readonly TryChargeDelegate _tryCharge;

    // Finds the nearest alive player within `gatherRadius` (a tile/Chebyshev coarse pre-filter — pass ⌈aggro⌉(+1)) of
    // `monster`. On success returns true and outputs the target id + its current continuous Position; false when none.
    public delegate bool FindTargetDelegate(WorldEntity monster, int gatherRadius, out ulong targetId, out WorldVector targetPosition);

    // Resolves a tracked target id to its live continuous Position + alive flag. False if the entity no longer exists.
    public delegate bool TryResolveTargetDelegate(ulong targetId, out WorldVector targetPosition, out bool alive);

    // Applies the monster's melee attack to the target id (face + damage + damage-number broadcast). Owned by
    // GameServer; the AI only decides WHEN (within AttackRangeUnits + cooldown).
    public delegate void AttackDelegate(WorldEntity monster, ulong targetId, int attackDamage);

    // MONSTER-BEHAVIOR P5 (docs/monster-behavior-design.md): START a CHARGE (a fast forward dash through the shared
    // ServerActionExecutor) on `monster` toward `heading` (a unit vector to the target) at `serverTick`. Owned by
    // GameServer (BeginMonsterCharge, which resolves the monster's TYPE for the dash distance / duration / cooldown);
    // the brain only decides WHEN (target out of attack range but within the trigger range, not fleeing). Returns
    // whether the charge actually STARTED — false if the executor's CanStart declined it (already in an action, or on
    // the charge cooldown), in which case the brain falls through to the normal approach. Null (the default dep) =
    // "never charge", so a behavior built without it (or for a non-charger type) is byte-identical to the pre-P5 brain.
    public delegate bool TryChargeDelegate(WorldEntity monster, WorldVector heading, uint serverTick);

    // MONSTER-BEHAVIOR P1 (docs/monster-behavior-design.md): the locomotion is no longer injected ONCE into the AI;
    // it is now passed PER STEP (StepMonster), resolved per-TYPE by GameServer from its locomotion registry. The AI is
    // locomotion-AGNOSTIC — told its "body" each tick — so a per-type body (P2 GlideLocomotion) needs no change here.
    // MONSTER-BEHAVIOR P5: `tryCharge` is the OPTIONAL charge-ability dep (default null = never charge). GameServer
    // injects the real one (BeginMonsterCharge) into BOTH the basicRoamer and skirmisher entries; a behavior built
    // without it — or one whose type's MonsterAiTunables.ChargeEnabled is false — never reaches a live charge call.
    public BasicRoamerBehavior(
        int seed,
        Func<TileCoord, bool> isWalkable,
        FindTargetDelegate findTarget,
        TryResolveTargetDelegate tryResolveTarget,
        AttackDelegate attack,
        TryChargeDelegate? tryCharge = null)
    {
        _random = new Random(seed);
        _isWalkable = isWalkable;
        _findTarget = findTarget;
        _tryResolveTarget = tryResolveTarget;
        _attack = attack;
        _tryCharge = tryCharge ?? ((WorldEntity _, WorldVector _, uint _) => false);
    }

    public int TrackedCount => _states.Count;

    // Registers a freshly spawned monster: records its spawn Position as the leash home and starts it Idle with an
    // initial randomized pause so it doesn't all-at-once lurch on the first eligible tick. The first aggro scan is
    // staggered by entity id (mod the scan interval) so a crowd of monsters doesn't scan on the same tick.
    public void Register(WorldEntity monster, uint serverTick, uint pauseMinTicks, uint pauseMaxTicks, uint aggroScanIntervalTicks)
    {
        var stagger = aggroScanIntervalTicks == 0 ? 0u : (uint)(monster.Id % aggroScanIntervalTicks);
        _states[monster.Id] = new MonsterState
        {
            Home = monster.Position,
            Phase = State.Idle,
            PauseUntilTick = serverTick + NextPauseTicks(pauseMinTicks, pauseMaxTicks),
            Destination = monster.Position,
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

    // Returns the leash home anchor (continuous) for a monster (test/diagnostic visibility). False if untracked.
    public bool TryGetHome(ulong monsterId, out WorldVector home)
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
    // invokes this each tick with the monster's effective move-cadence ticks; the locomotion's hop gate drops a
    // too-early hop on cooldown, so the monster physically leaps at most once per cadence. The pause / attack /
    // aggro-scan timers are measured in ticks too, so the whole behaviour is tick-quantised.
    //
    // Returns true iff the monster committed a HOP this call (HopResult.Moved) — diagnostic only; replication rides
    // StateRevision, which ApplyResolvedMove (inside the locomotion) bumps only when the rounded tile actually crosses,
    // so the snapshot cadence/bandwidth stay tile-keyed at today's rate regardless of this flag. The GameServer pass
    // ignores the return; it is the headless tests' "did it move" signal.
    public bool StepMonster(
        WorldEntity monster, uint serverTick, uint stepCooldownTicks, in MonsterAiTunables t, IMonsterLocomotion locomotion)
    {
        if (!_states.TryGetValue(monster.Id, out var state))
        {
            return false;
        }

        // AGGRO (throttled): scan for a player in range and switch to Chasing on a hit — ONLY from Idle/Roaming.
        // NOT while Chasing (already has a target), and NOT while Returning: a leashed/evading monster commits to
        // reaching home before it re-evaluates aggro. Throttled per monster (NextAggroScanTick), staggered at Register.
        if ((state.Phase is State.Idle or State.Roaming) && serverTick >= state.NextAggroScanTick)
        {
            // Pass a COARSE tile-radius pre-filter = ⌈aggro⌉(+1) so the spatial gather drops no in-range Euclidean
            // target; the precise Euclidean range test happens below on the target's continuous Position.
            var gatherRadius = GatherRadiusFor(t.AggroRadius);
            if (gatherRadius > 0
                && _findTarget(monster, gatherRadius, out var foundId, out var foundPos)
                && Distance(monster.Position, foundPos) <= t.AggroRadius)
            {
                EnterChase(ref state, foundId, serverTick);
            }

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
                        // Fall through to take the first roam hop THIS tick (the pause already elapsed).
                        moved = StepTowardDestination(monster, locomotion, ref state, serverTick, stepCooldownTicks, t);
                    }
                    else
                    {
                        // Boxed in — no open tile in the leash. Stay Idle, re-pause so we re-test later.
                        state.PauseUntilTick = serverTick + NextPauseTicks(t.PauseMinTicks, t.PauseMaxTicks);
                    }
                }

                break;

            case State.Roaming:
                moved = StepTowardDestination(monster, locomotion, ref state, serverTick, stepCooldownTicks, t);
                break;

            case State.Chasing:
                moved = StepChase(monster, locomotion, ref state, serverTick, stepCooldownTicks, t);
                break;

            case State.Returning:
                // Returning hops toward Destination = home (set in BeginReturnHome); same machinery as roam.
                moved = StepTowardDestination(monster, locomotion, ref state, serverTick, stepCooldownTicks, t);
                break;
        }

        _states[monster.Id] = state;
        return moved;
    }

    // Enters Chasing a target. The chase target is re-read live each hop from the target's CURRENT Position, so
    // Destination here is moot; LastProgressTick starts the no-progress watchdog. The attack cooldown is NOT reset on
    // entering chase (its own timer still governs) — Register seeds NextAttackTick = spawn tick so the first hit lands.
    private void EnterChase(ref MonsterState state, ulong targetId, uint serverTick)
    {
        state.Phase = State.Chasing;
        state.TargetId = targetId;
        state.TargetPresent = true;
        state.LastProgressTick = serverTick;
    }

    // One chase tick. (1) Resolve the target and apply the leash rules (Euclidean) → Returning on any break. (2) If
    // within AttackRangeUnits and the attack cooldown elapsed, attack on the OWN attack timer (no hop this tick). (3)
    // Otherwise hop toward the target's current Position, with the no-progress watchdog.
    private bool StepChase(
        WorldEntity monster, IMonsterLocomotion locomotion, ref MonsterState state, uint serverTick, uint stepCooldownTicks, in MonsterAiTunables t)
    {
        // (1) De-aggro checks. Target gone/dead, OR beyond the de-aggro range, OR the monster pulled farther than
        // chaseLeash from home → drop aggro and walk home. All Euclidean on Position.
        if (!_tryResolveTarget(state.TargetId, out var targetPos, out var alive) || !alive)
        {
            BeginReturnHome(locomotion, monster, ref state, serverTick);
            return false;
        }

        var distToTarget = Distance(monster.Position, targetPos);
        var distFromHome = Distance(monster.Position, state.Home);
        if (distToTarget > t.DeaggroRadius || distFromHome > t.ChaseLeash)
        {
            BeginReturnHome(locomotion, monster, ref state, serverTick);
            return false;
        }

        // (1.5) FLEE hook (MONSTER-BEHAVIOR P4). Placed AFTER the leash/de-aggro checks (so a fleeing monster still
        // gives up + returns home when pulled past the de-aggro/leash bounds) and BEFORE the attack/approach branch.
        // Default returns false → the rest of the chase runs UNCHANGED (BasicRoamer is byte-identical). A subclass
        // (SkirmisherBehavior) returns true + a flee destination to OVERRIDE this tick: move AWAY (toward fleeTarget)
        // via the SAME locomotion-move-with-watchdog helper the approach uses, and SKIP the attack. Sharing the helper
        // is what preserves the watchdog/progress bookkeeping so a fleeing monster never false-trips the no-progress
        // bail (and a fleer wedged into a wall still bails to Returning via that watchdog, just like the approach).
        if (TryChooseFleeTarget(monster, t, targetPos, out var fleeTarget))
        {
            return StepMoveTowardWithWatchdog(monster, locomotion, ref state, serverTick, stepCooldownTicks, fleeTarget);
        }

        // (1.6) CHARGE trigger (MONSTER-BEHAVIOR P5). A SHARED, config-gated ability (NOT skirmisher-specific): placed
        // AFTER the flee hook (so a wounded/fleeing charger flees, never charges — flee takes precedence) and BEFORE the
        // attack/approach branch. Fire a charge ONLY when the type composed it (ChargeEnabled) AND the target is OUT of
        // attack range BUT within the trigger range (the gap is worth a dash). The executor's CanStart also gates it
        // (already in an action, or on the charge cooldown), so a declined charge falls THROUGH to the normal approach;
        // a started one returns for this tick — the executor now owns the dash and the glide self-guards (no double-move)
        // until it ends. Count a started charge as progress so the no-progress watchdog never trips on the dash. When
        // ChargeEnabled is false (every BasicRoamer/slime + a non-charger gnoll) this block is INERT → byte-identical.
        if (t.ChargeEnabled && distToTarget > t.AttackRangeUnits && distToTarget <= t.ChargeTriggerRangeUnits)
        {
            var dirToTarget = (targetPos - monster.Position).Normalized();
            if (dirToTarget != WorldVector.Zero && _tryCharge(monster, dirToTarget, serverTick))
            {
                state.LastProgressTick = serverTick;
                return false;
            }
        }

        // (2) Within attack range + off cooldown → attack. The attack is the monster's OWN per-monster timer,
        // independent of its move cadence. We do NOT hop on an attack tick. Face the target while in range.
        if (distToTarget <= t.AttackRangeUnits)
        {
            // MONSTER-BEHAVIOR P2: in range — STOP moving to attack. Zero a velocity-based body's Velocity at this stop
            // edge so a glider doesn't keep extrapolating into/through its target while it stands and swings (a no-op
            // for the hop; idempotent — a second Stop on an already-stopped body does nothing).
            locomotion.Stop(monster);
            monster.SetFacingFromUnit((targetPos - monster.Position).Normalized());
            if (serverTick >= state.NextAttackTick)
            {
                _attack(monster, state.TargetId, t.AttackDamage);
                state.NextAttackTick = serverTick + t.AttackCooldownTicks;
                state.LastProgressTick = serverTick; // in-range-and-attacking counts as progress (not wedged).
            }

            return false;
        }

        // (3) Hop toward the target's current Position via the shared move-with-watchdog helper (the locomotion arms
        // the cadence + faces the heading). The flee branch (1.5) calls the SAME helper toward its flee destination.
        return StepMoveTowardWithWatchdog(monster, locomotion, ref state, serverTick, stepCooldownTicks, targetPos);
    }

    // One chase MOVE toward `moveTarget` (the target's Position when approaching, or a flee destination when a
    // subclass is fleeing) with the no-progress watchdog. Extracted from StepChase so the normal approach and the
    // MONSTER-BEHAVIOR P4 flee path share IDENTICAL move + watchdog bookkeeping: a Moved hop resets the watchdog, a
    // Stuck hop (a slide fixpoint the fan can't escape) trips it once it has persisted past any legitimate cadence
    // wait and bails to Returning (so a monster — approaching OR fleeing — can never freeze against a wall), and an
    // OnCooldown tick is a harmless wait. Behaviourally byte-identical to the former inline approach branch.
    private bool StepMoveTowardWithWatchdog(
        WorldEntity monster, IMonsterLocomotion locomotion, ref MonsterState state, uint serverTick, uint stepCooldownTicks, WorldVector moveTarget)
    {
        var result = locomotion.Advance(monster, moveTarget, serverTick, stepCooldownTicks);
        switch (result)
        {
            case HopResult.Moved:
                state.LastProgressTick = serverTick;
                return true;

            case HopResult.Stuck:
                // Wedged (a slide fixpoint the fan can't escape). The watchdog bails to Returning so a monster can
                // never freeze against a wall — it walks home, re-roams, and can re-aggro on the next scan.
                if (NoProgressTimedOut(state.LastProgressTick, serverTick, stepCooldownTicks))
                {
                    BeginReturnHome(locomotion, monster, ref state, serverTick);
                }

                return false;

            default: // OnCooldown — harmless wait.
                return false;
        }
    }

    // MONSTER-BEHAVIOR P4 (docs/monster-behavior-design.md): the ONE overridable chase DECISION hook. Called each
    // Chasing tick AFTER the leash/de-aggro checks and BEFORE the attack/approach branch. DEFAULT: never flee — return
    // false so the base brain (BasicRoamer / the slime) approaches + attacks exactly as before (byte-identical). A
    // subclass returns true + a flee destination to OVERRIDE this tick: the monster moves toward `fleeTarget` (glides
    // AWAY from the target) via the shared move-with-watchdog helper and does NOT attack this tick, while keeping the
    // rest of the state machine (leash, de-aggro, watchdog). `targetPos` is the chased target's current Position.
    protected virtual bool TryChooseFleeTarget(WorldEntity monster, in MonsterAiTunables t, WorldVector targetPos, out WorldVector fleeTarget)
    {
        fleeTarget = default;
        return false;
    }

    // Drop aggro and head home. Destination = home; Returning hops there and resumes Idle on arrival.
    private void BeginReturnHome(IMonsterLocomotion locomotion, WorldEntity monster, ref MonsterState state, uint serverTick)
    {
        // MONSTER-BEHAVIOR P2: zero a velocity-based body's Velocity at the chase→return TURN edge so a glider's
        // replicated velocity (pointing at the abandoned target, or into the wall it wedged on) doesn't extrapolate
        // the wrong way for the seam tick before Returning re-aims it at home next tick. A no-op for the hop.
        locomotion.Stop(monster);
        state.Phase = State.Returning;
        state.TargetPresent = false;
        state.Destination = state.Home;
        state.LastProgressTick = serverTick;
        // Re-scan for aggro promptly once we start returning (don't wait a full interval) so a player who steps back
        // into range mid-return re-aggros quickly; staggered scan still applies on subsequent ticks.
        state.NextAggroScanTick = serverTick;
    }

    // One hop toward the destination (the roam target while Roaming, or the home anchor while Returning — both held in
    // state.Destination). Hops via the locomotion; on ARRIVAL (within the progress epsilon of the destination), or on
    // a wedge (HopResult.Stuck + the no-progress watchdog), flips back to Idle with a fresh pause. An OnCooldown tick
    // is a harmless wait.
    private bool StepTowardDestination(
        WorldEntity monster,
        IMonsterLocomotion locomotion,
        ref MonsterState state,
        uint serverTick,
        uint stepCooldownTicks,
        in MonsterAiTunables t)
    {
        // Arrival: within the progress epsilon of the destination — close enough; the hop can't meaningfully advance.
        if (Distance(monster.Position, state.Destination) <= HopLocomotion.ProgressEpsilonUnits)
        {
            GoIdle(locomotion, monster, ref state, serverTick, t.PauseMinTicks, t.PauseMaxTicks);
            return false;
        }

        var result = locomotion.Advance(monster, state.Destination, serverTick, stepCooldownTicks);

        switch (result)
        {
            case HopResult.Moved:
                state.LastProgressTick = serverTick;
                // If that hop landed us on (within epsilon of) the destination, go Idle; otherwise keep moving.
                if (Distance(monster.Position, state.Destination) <= HopLocomotion.ProgressEpsilonUnits)
                {
                    GoIdle(locomotion, monster, ref state, serverTick, t.PauseMinTicks, t.PauseMaxTicks);
                }

                return true;

            case HopResult.Stuck:
                // Wedged this tick (no progress). The watchdog bails once the wedge has persisted longer than any
                // legitimate cadence wait — flip back to Idle with a fresh pause and re-pick a destination next pass.
                if (NoProgressTimedOut(state.LastProgressTick, serverTick, stepCooldownTicks))
                {
                    GoIdle(locomotion, monster, ref state, serverTick, t.PauseMinTicks, t.PauseMaxTicks);
                }

                return false;

            default: // OnCooldown — harmless wait; do not touch the watchdog, do not advance progress.
                return false;
        }
    }

    // True once the monster has gone longer than ~2 move windows + a margin without a Moved hop — longer than any
    // legitimate cadence wait can explain, so a wedge (a slide fixpoint the fan can't escape) is detected and the
    // caller bails instead of spinning. Margin (+1) absorbs tick-rounding so a monster on a normal cadence is never
    // falsely flagged. A Stuck hop does NOT advance LastProgressTick (the locomotion attempted and failed), so a
    // persistent wedge reliably trips this; an OnCooldown wait is not Stuck, so it never falsely accumulates.
    private static bool NoProgressTimedOut(uint lastProgressTick, uint serverTick, uint stepCooldownTicks)
    {
        return serverTick > lastProgressTick && (serverTick - lastProgressTick) > stepCooldownTicks * 2 + 1;
    }

    private void GoIdle(IMonsterLocomotion locomotion, WorldEntity monster, ref MonsterState state, uint serverTick, uint pauseMinTicks, uint pauseMaxTicks)
    {
        // MONSTER-BEHAVIOR P2: zero a velocity-based body's Velocity at the stop edge into Idle (arrival or a wedge
        // bail) so a glider parks cleanly at its final position and the client stops extrapolating. A no-op for the hop.
        locomotion.Stop(monster);
        state.Phase = State.Idle;
        state.TargetPresent = false;
        state.PauseUntilTick = serverTick + NextPauseTicks(pauseMinTicks, pauseMaxTicks);
    }

    // Picks a random OPEN continuous point within Euclidean `roamRadius` of home (the leash), at least one progress
    // epsilon away from the monster's current Position (so a roam is always a real move). Samples uniformly in the
    // disc (validating the rounded tile is walkable), then falls back to a deterministic scan of the leash box so a
    // sparse-but-non-empty leash still yields a target; returns false only when the whole leash box is unwalkable. The
    // chosen point being within roamRadius of home is what KEEPS the monster leashed during roam: a hop toward an
    // in-radius point never leaves it (modulo the one-hop overshoot the leash tolerates).
    private bool TryPickRoamDestination(WorldEntity monster, WorldVector home, double roamRadius, out WorldVector destination)
    {
        destination = monster.Position;
        if (roamRadius <= 0d)
        {
            return false;
        }

        const int probes = 12;
        for (var i = 0; i < probes; i++)
        {
            // Uniform sample in the disc: radius = R·√u (area-uniform), angle uniform. Deterministic from _random.
            var u = _random.NextDouble();
            var angle = _random.NextDouble() * 2d * Math.PI;
            var r = roamRadius * Math.Sqrt(u);
            var candidate = new WorldVector(home.X + (r * Math.Cos(angle)), home.Y + (r * Math.Sin(angle)));
            if (Distance(candidate, monster.Position) > HopLocomotion.ProgressEpsilonUnits
                && _isWalkable(candidate.ToTileRounded()))
            {
                destination = candidate;
                return true;
            }
        }

        // Deterministic fallback: scan the integer tiles of the leash bounding box (starting at a per-monster offset),
        // accept the first walkable tile centre that is within the Euclidean radius and not the current tile. Keeps a
        // boxed-in monster terminating (the original bounded-probe → deterministic-scan fallback, on the disc).
        var radiusTiles = (int)Math.Ceiling(roamRadius);
        var span = (radiusTiles * 2) + 1;
        var cellCount = span * span;
        var homeTile = home.ToTileRounded();
        var currentTile = monster.TileCoord;
        var start = (int)((uint)(_random.Next() ^ (int)monster.Id) % (uint)cellCount);
        for (var k = 0; k < cellCount; k++)
        {
            var index = (start + k) % cellCount;
            var dx = (index % span) - radiusTiles;
            var dy = (index / span) - radiusTiles;
            var candidateTile = homeTile.Offset(dx, dy);
            var candidate = WorldVector.FromTile(candidateTile);
            if (candidateTile != currentTile
                && Distance(candidate, home) <= roamRadius
                && _isWalkable(candidateTile))
            {
                destination = candidate;
                return true;
            }
        }

        return false;
    }

    // Euclidean distance between two continuous positions — the metric the leash / adjacency / aggro / de-aggro all use
    // (see the class-header conversion table). Replaces the old Chebyshev tile metric.
    private static double Distance(WorldVector a, WorldVector b) => (a - b).Length;

    // The coarse tile/Chebyshev gather radius for the aggro pre-filter: ⌈Euclidean aggro⌉ + 1, so the spatial scan's
    // box is a strict superset of the Euclidean aggro disc and never drops an in-range target before the precise test.
    private static int GatherRadiusFor(double aggroRadius)
    {
        if (aggroRadius <= 0d)
        {
            return 0;
        }

        return (int)Math.Ceiling(aggroRadius) + 1;
    }

    // A random pause length in [min, max] ticks (inclusive). Floored at 1 so a degenerate min/max can never produce
    // a zero-pause loop that would hop every tick.
    private uint NextPauseTicks(uint pauseMinTicks, uint pauseMaxTicks)
    {
        var lo = Math.Max(1u, pauseMinTicks);
        var hi = Math.Max(lo, pauseMaxTicks);
        return (uint)_random.Next((int)lo, (int)hi + 1);
    }
}
