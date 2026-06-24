using Mmo.Shared.Domain;

namespace Mmo.Server.Runtime;

// LIVING-ENEMIES P1: the server-side "leashed idle-wander" brain for EntityKind.Monster. One instance owns ALL
// monsters' per-entity AI state (keyed by entity id) plus the shared PRNG, and steps them on the server tick.
// It is deliberately decoupled from GameServer plumbing so it can be unit-tested directly against a WorldState
// + a walkability oracle (no network, no sessions):
//
//   * Each monster has a HOME anchor (the tile it was spawned at), a roam RADIUS (Chebyshev tiles from home it
//     may wander within — the leash), and a small state machine: Idle (standing still until a pause timer
//     elapses) ↔ Roaming (walking one tile per step cadence toward a chosen open destination).
//   * The monster moves through the SAME WorldEntity tile-step path players use (Zone.TryStep here, via the
//     injected stepper), so facing / StepSequence / AOI migration / replication / client interpolation all work
//     for free — there is NO parallel movement system.
//
// FEEL: a monster is IDLE (still) most of the time. When its pause timer elapses it picks a random OPEN tile
// within the leash, walks to it ONE tile per step-cooldown, then goes Idle again for a fresh randomized pause.
// It is paced off the step cooldown exactly like the held-movement pacer, so it never steps every tick.
//
// DETERMINISM: the AI owns a seeded System.Random so tests are reproducible. The per-monster destination pick
// also folds in the entity id so two monsters seeded from the same instance don't roam in lockstep.
//
// NO aggro / chase / attack / death / respawn this phase — that is Phase 2+. This is ROAM only.
public sealed class MonsterRoamAi
{
    public enum State : byte
    {
        Idle = 0,
        Roaming = 1,
    }

    // Per-monster AI record. Mutable struct held by value in the dictionary and written back on change — the
    // state set is tiny (a handful of monsters) so a dictionary keyed by entity id is the simplest store and
    // costs nothing measurable. Home is the leash centre; PauseUntilTick gates the next Idle→Roam transition;
    // Destination is the current roam target (meaningful only while Roaming).
    private struct MonsterState
    {
        public TileCoord Home;
        public State Phase;
        public uint PauseUntilTick;
        public TileCoord Destination;
    }

    private readonly Dictionary<ulong, MonsterState> _states = [];
    private readonly Random _random;

    // The walkability oracle (Zone.IsWalkable) and the single-tile stepper (Zone.TryStep). Injected so the AI is
    // testable against a bare TileGrid/WorldState without a live Zone/GameServer. The stepper returns whether the
    // tile actually advanced; the AI uses that to detect "blocked / made no progress" and bail back to Idle.
    private readonly Func<TileCoord, bool> _isWalkable;
    private readonly TryStepDelegate _tryStep;

    // The stepper signature mirrors Zone.TryStep (entity, direction, serverTick, stepCooldownTicks) → accepted.
    public delegate bool TryStepDelegate(WorldEntity entity, Direction8 direction, uint serverTick, uint stepCooldownTicks);

    public MonsterRoamAi(int seed, Func<TileCoord, bool> isWalkable, TryStepDelegate tryStep)
    {
        _random = new Random(seed);
        _isWalkable = isWalkable;
        _tryStep = tryStep;
    }

    public int TrackedCount => _states.Count;

    // Registers a freshly spawned monster: records its spawn tile as the leash home and starts it Idle with an
    // initial randomized pause so it doesn't all-at-once lurch on the first eligible tick. Re-registering an id
    // (shouldn't happen) just resets it. `serverTick` is the current tick the initial pause is measured from.
    public void Register(WorldEntity monster, uint serverTick, uint pauseMinTicks, uint pauseMaxTicks)
    {
        _states[monster.Id] = new MonsterState
        {
            Home = monster.Tile,
            Phase = State.Idle,
            PauseUntilTick = serverTick + NextPauseTicks(pauseMinTicks, pauseMaxTicks),
            Destination = monster.Tile,
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

    // Steps ONE tracked monster for this tick. Paced by the caller exactly like StepHeldMovementIntents: the
    // caller invokes this each tick with the monster's effective step-cooldown ticks; the underlying TryStep gate
    // drops a too-early step on cooldown, so the monster physically advances at most one tile per cooldown. The
    // pause timers are measured in ticks too, so the whole behaviour is tick-quantised and frame-rate independent.
    //
    //   Idle:    do nothing until PauseUntilTick. Then pick a random OPEN tile within `roamRadius` of home as the
    //            destination and switch to Roaming. If no open tile is found (boxed in), stay Idle and re-pause.
    //   Roaming: step ONE tile greedily toward the destination (reduce the larger of |dx|,|dy|; diagonals
    //            allowed). On arrival, OR if the step was rejected (wall / cooldown-independent block) so no
    //            progress is possible, return to Idle with a fresh randomized pause.
    //
    // Returns true iff the monster's tile actually advanced this call (so the caller can mark replication / trace).
    public bool StepMonster(
        WorldEntity monster,
        uint serverTick,
        uint stepCooldownTicks,
        int roamRadius,
        uint pauseMinTicks,
        uint pauseMaxTicks)
    {
        if (!_states.TryGetValue(monster.Id, out var state))
        {
            return false;
        }

        var moved = false;

        switch (state.Phase)
        {
            case State.Idle:
                if (serverTick >= state.PauseUntilTick)
                {
                    if (TryPickRoamDestination(monster, state.Home, roamRadius, out var destination))
                    {
                        state.Destination = destination;
                        state.Phase = State.Roaming;
                        // Fall through to take the first roam step THIS tick (the pause already elapsed).
                        moved = StepTowardDestination(monster, ref state, serverTick, stepCooldownTicks, pauseMinTicks, pauseMaxTicks);
                    }
                    else
                    {
                        // Boxed in — no open tile in the leash. Stay Idle, re-pause so we re-test later.
                        state.PauseUntilTick = serverTick + NextPauseTicks(pauseMinTicks, pauseMaxTicks);
                    }
                }

                break;

            case State.Roaming:
                moved = StepTowardDestination(monster, ref state, serverTick, stepCooldownTicks, pauseMinTicks, pauseMaxTicks);
                break;
        }

        _states[monster.Id] = state;
        return moved;
    }

    // One greedy tile-step toward the destination. Reduces whichever axis distance is larger (allowing diagonals
    // when both axes still differ), maps that (dx,dy) to a Direction8, and routes it through the SAME stepper
    // players use. On arrival, or when the step did not advance the tile (blocked at a wall / the cooldown gate
    // would only delay, but a true block means no progress), flip back to Idle with a fresh pause. The cooldown
    // gate inside TryStep makes a too-early call a harmless no-op that simply re-tries next tick — that does NOT
    // end the roam; only reaching the destination or being physically blocked does.
    private bool StepTowardDestination(
        WorldEntity monster,
        ref MonsterState state,
        uint serverTick,
        uint stepCooldownTicks,
        uint pauseMinTicks,
        uint pauseMaxTicks)
    {
        if (monster.Tile == state.Destination)
        {
            GoIdle(ref state, serverTick, pauseMinTicks, pauseMaxTicks);
            return false;
        }

        var direction = GreedyDirectionToward(monster.Tile, state.Destination);
        var before = monster.Tile;
        var stepped = _tryStep(monster, direction, serverTick, stepCooldownTicks);

        if (stepped)
        {
            // Advanced one tile. If that landed us on the destination, go Idle; otherwise keep Roaming.
            if (monster.Tile == state.Destination)
            {
                GoIdle(ref state, serverTick, pauseMinTicks, pauseMaxTicks);
            }

            return true;
        }

        // Not stepped. Distinguish "cooldown — just wait" from "blocked — give up": a cooldown drop leaves the
        // tile unchanged but the NEXT direction tile is walkable, so re-test next tick (stay Roaming). A true
        // block (wall / out of bounds toward the destination) means greedy progress is impossible from here →
        // bail to Idle so the monster never wedges itself against a wall forever.
        var delta = direction.Delta();
        var nextTile = before.Offset(delta.X, delta.Y);
        if (!_isWalkable(nextTile))
        {
            GoIdle(ref state, serverTick, pauseMinTicks, pauseMaxTicks);
        }

        return false;
    }

    private void GoIdle(ref MonsterState state, uint serverTick, uint pauseMinTicks, uint pauseMaxTicks)
    {
        state.Phase = State.Idle;
        state.PauseUntilTick = serverTick + NextPauseTicks(pauseMinTicks, pauseMaxTicks);
    }

    // Picks a random OPEN tile within Chebyshev `roamRadius` of home (the leash), excluding the monster's current
    // tile (so a roam is always a real move). Tries a bounded number of random offsets, then falls back to a
    // deterministic scan of the leash box so a sparse-but-non-empty leash still yields a target; returns false
    // only when the entire leash box (minus the current tile) is unwalkable. The chosen tile being within
    // roamRadius of home is what KEEPS the monster leashed: every destination is inside the radius, and a greedy
    // walk toward an in-radius tile never leaves the radius.
    private bool TryPickRoamDestination(WorldEntity monster, TileCoord home, int roamRadius, out TileCoord destination)
    {
        destination = monster.Tile;
        if (roamRadius <= 0)
        {
            return false;
        }

        // A handful of random probes first (cheap, gives natural variety). Each probe is a uniform offset in
        // [-radius, +radius] on each axis → uniformly inside the Chebyshev leash box.
        const int probes = 12;
        for (var i = 0; i < probes; i++)
        {
            var candidate = home.Offset(
                _random.Next(-roamRadius, roamRadius + 1),
                _random.Next(-roamRadius, roamRadius + 1));
            if (candidate != monster.Tile && _isWalkable(candidate))
            {
                destination = candidate;
                return true;
            }
        }

        // Deterministic fallback scan of the leash box (random start offset folded with entity id so monsters
        // don't all converge on the same tile). Guarantees we find an open tile if one exists.
        var span = roamRadius * 2 + 1;
        var cellCount = span * span;
        var start = (int)((uint)(_random.Next() ^ (int)monster.Id) % (uint)cellCount);
        for (var k = 0; k < cellCount; k++)
        {
            var index = (start + k) % cellCount;
            var dx = (index % span) - roamRadius;
            var dy = (index / span) - roamRadius;
            var candidate = home.Offset(dx, dy);
            if (candidate != monster.Tile && _isWalkable(candidate))
            {
                destination = candidate;
                return true;
            }
        }

        return false;
    }

    // Greedy one-tile direction from `from` toward `to`: sign of each axis delta, allowing a diagonal when both
    // axes still differ. Reaches the destination in max(|dx|,|dy|) steps (the Chebyshev distance) — the natural
    // 8-direction walk. `from == to` is never passed here (the caller checks arrival first), but defaults to S.
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

    // A random pause length in [min, max] ticks (inclusive). Floored at 1 so a degenerate min/max can never
    // produce a zero-pause loop that would step every tick.
    private uint NextPauseTicks(uint pauseMinTicks, uint pauseMaxTicks)
    {
        var lo = Math.Max(1u, pauseMinTicks);
        var hi = Math.Max(lo, pauseMaxTicks);
        // Random.Next upper bound is exclusive → +1 for an inclusive [lo, hi].
        return (uint)_random.Next((int)lo, (int)hi + 1);
    }
}
