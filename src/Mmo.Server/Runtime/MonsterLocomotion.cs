using Mmo.Shared.Domain;

namespace Mmo.Server.Runtime;

// CONTINUOUS MIGRATION (Phase 8): the monster MOVEMENT-STYLE seam. The roam/chase AI (MonsterRoamAi) decides WHERE a
// monster wants to go (a continuous WorldVector target) and WHEN it may move (its own state machine + timers); the
// locomotion decides HOW it gets there for one move-cadence window. Ship ONLY HopLocomotion this phase: a discrete,
// collision-valid LEAP (the slime keeps hopping). A future GlideLocomotion (sets Velocity, integrates per-tick) would
// implement the SAME one method and slot in without touching the nav state machine — but is NOT built now (do not
// over-build). The AI never sees the geometry; it only reads the HopResult to drive its livelock watchdog.
public interface IMonsterLocomotion
{
    // Advance `monster` toward `target` for THIS server tick. Returns:
    //   OnCooldown — the move cadence has not elapsed; Position is UNCHANGED; the AI waits (no progress test).
    //   Moved      — a hop committed and the landing advanced >= progress epsilon toward the target; the AI marks
    //                progress (resets its no-progress watchdog).
    //   Stuck      — the cadence elapsed and a hop (primary direction + the clear-direction fan) was attempted, but
    //                the best landing made < epsilon progress (a slide fixpoint / fully wedged). The cadence is left
    //                UN-ARMED (re-try next tick, mirroring TryStep's blocked-step rule) and the AI treats it as
    //                NO-progress so its watchdog eventually fires. Position may have moved a hair (< epsilon) or not.
    HopResult Advance(WorldEntity monster, WorldVector target, uint serverTick, uint cooldownTicks);
}

// The outcome of one IMonsterLocomotion.Advance call — see the per-value notes on Advance. Distinguishing OnCooldown
// from Stuck is the linchpin of the Phase-8 livelock fix: a wait must NOT look like no-progress, but a slide fixpoint
// MUST (so the watchdog fires at a wall the resolver can't slide past).
public enum HopResult : byte
{
    OnCooldown = 0,
    Moved = 1,
    Stuck = 2,
}

// CONTINUOUS MIGRATION (Phase 8): the slime locomotion — a discrete, collision-valid LEAP of HopDistanceUnits toward
// the target once per move-cadence window, with Velocity left at Zero (the sparse-update "jump" is preserved; monsters
// stay OFF the player velocity-glide path). One hop:
//   1. dir   = (target - from).Normalized()                                  // true unit heading (not a Direction8 snap)
//   2. delta = dir * HopDistanceUnits
//   3. walls = QueryWalls(from, delta, radius)                               // shared TileWalls, the SAME the player uses
//   4. land  = ContinuousCollision.Resolve(from, delta, radius, walls)       // slide/stop, anti-tunnel — collision-VALID
//   5. if land made < epsilon progress toward target, try the clear-direction FAN (±45°, ±90°, deterministic order)
//      and take the first candidate whose resolved landing makes positive (>= epsilon) progress.
//   6. commit: ApplyResolvedMove(best); face from the unit heading; ARM the cadence (TryBeginHop) — only on a Moved.
// The landing is guaranteed collision-valid by the resolver (never inside a wall AABB within the body radius, never
// tunneled through one), so the S75 corner-cut rule is obtained for free, continuously. Body radius is the SAME the
// players collide at (ServerTuning.BodyRadiusUnits), so a monster fits exactly where a player does.
//
// CADENCE: IsHopReady gates the whole attempt; an accepted (Moved) hop arms _nextEligibleTick = serverTick + cooldown
// via TryBeginHop; a fully-blocked (Stuck) attempt leaves the gate un-armed so it re-tests next tick — TryStep's exact
// arm/re-try rule, now owned here.
//
// DETERMINISM: the fan order is fixed (±45°, ±90°), the resolver is all-double + RNG-free, and the wall query is the
// stable row-major shared helper — so a given (from, target, walls) yields a byte-identical landing every call.
public sealed class HopLocomotion : IMonsterLocomotion
{
    // The minimum Euclidean displacement toward the target that counts a hop as real PROGRESS (vs a slide fixpoint).
    // ~0.1 tile: comfortably below a full HopDistance leap, comfortably above floating-point slide residue. Shared by
    // the fan (which candidate "makes progress") and the Moved/Stuck verdict the AI's watchdog reads. PINNED with the
    // watchdog's epsilon — they are the same notion of "did it actually advance".
    public const double ProgressEpsilonUnits = 0.1d;

    // The clear-direction FAN: rotations (radians) applied to the primary heading, in deterministic order, tried when
    // a straight hop slides to near-zero progress (the resolved landing hit a perpendicular wall). ±45° first (a gentle
    // detour), then ±90° (squeeze along the wall). NOT a navmesh — lightweight local steering. The first candidate with
    // positive progress wins; order is fixed so the path is reproducible.
    private static readonly double[] FanRotations = { Deg(45), Deg(-45), Deg(90), Deg(-90) };

    // Hop distance + body radius are read FRESH per Advance (via providers) so a live retune of the per-type hop
    // distance or "continuous.bodyRadius" takes effect on the next hop — consistent with the player integrator (which
    // reads BodyRadiusUnits fresh each input) and the AI's other live per-type Tunables. PINNED to the player radius so
    // a monster fits exactly where a player does.
    private readonly Func<double> _hopDistanceUnits;
    private readonly Func<double> _bodyRadiusUnits;
    private readonly QueryWallsDelegate _queryWalls;
    private readonly BeginHopDelegate _beginHop;
    private readonly Func<ulong, bool> _isActionActive;
    private readonly List<ContinuousCollision.Wall> _wallScratch = new();

    // Fills `scratch` with the collision walls near a swept move (from, delta, radius) in stable row-major order — the
    // SAME TileGrid.QueryNearbyWalls / TileWalls helper the player integrator uses, injected so the locomotion is unit-
    // testable against a bare TileGrid without a live Zone.
    public delegate void QueryWallsDelegate(
        WorldVector start, WorldVector delta, double radius, List<ContinuousCollision.Wall> scratch);

    // MOVEMENT-ACTIONS (Phase C): START a real ballistic Jump on `monster` toward `heading` for `hopDistance` units over
    // `cooldownTicks` ticks (the move cadence — the arc spans the whole window then lands), at `serverTick`. Injected
    // (GameServer.BeginMonsterHop / a test) so the locomotion stays decoupled from the shared ServerActionExecutor that
    // actually advances the arc per tick (XY through the shared resolver, Z via the ballistic formula → the replicated
    // VerticalOffset). REPLACES the old instant-teleport apply: the locomotion now DECIDES the hop (direction + that it
    // makes progress) and hands the MOVEMENT to the executor. Returns whether the action actually started (the executor
    // accepted the trigger); false ⇒ treat as no-progress (should not happen given the IsActive gate below).
    public delegate bool BeginHopDelegate(
        WorldEntity monster, WorldVector heading, double hopDistance, uint cooldownTicks, uint serverTick);

    public HopLocomotion(
        Func<double> hopDistanceUnits,
        Func<double> bodyRadiusUnits,
        QueryWallsDelegate queryWalls,
        BeginHopDelegate beginHop,
        Func<ulong, bool> isActionActive)
    {
        _hopDistanceUnits = hopDistanceUnits;
        _bodyRadiusUnits = bodyRadiusUnits;
        _queryWalls = queryWalls;
        _beginHop = beginHop;
        _isActionActive = isActionActive;
    }

    public HopResult Advance(WorldEntity monster, WorldVector target, uint serverTick, uint cooldownTicks)
    {
        // MOVEMENT-ACTIONS (Phase C): a hop arc is in flight (the executor owns the monster's movement mid-jump) — the
        // tick loop runs StepMonsterAi BEFORE StepAll, so without this gate the monster could try to re-hop on the very
        // tick its arc ends (executor.CanStart would reject it, but TryBeginHop would already be armed → cadence desync).
        // Treat an in-flight action as a harmless cadence wait (OnCooldown): it must NOT trip the livelock watchdog (the
        // monster IS making progress along the arc) and must NOT count as a roam/return arrival pre-check failure.
        if (_isActionActive(monster.Id))
        {
            return HopResult.OnCooldown;
        }

        // Gate the whole attempt on the move cadence (read-only) so a fully-blocked hop can leave the gate un-armed.
        if (!monster.IsHopReady(serverTick))
        {
            return HopResult.OnCooldown;
        }

        var from = monster.Position;
        var toTarget = target - from;
        if (toTarget.LengthSquared <= 0d)
        {
            // Already on the target (the AI checks arrival/adjacency first, so this is a guard). No move, no arm.
            return HopResult.Stuck;
        }

        // Snapshot the live knobs ONCE per Advance so the primary hop and the whole fan use a consistent distance +
        // radius (a retune mid-fan would otherwise compare candidates on different geometry). CLAMP the leap to the
        // remaining distance to the target so a hop NEVER overshoots a nearby destination — without this a fixed-1.0
        // leap toward a point closer than 1.0 would oscillate across it forever (it would never land within the
        // arrival epsilon). The clamp only ever SHORTENS the final approach hop; a far target hops the full distance.
        var radius = _bodyRadiusUnits();
        var hopDistance = Math.Min(_hopDistanceUnits(), toTarget.Length);

        var primaryDir = toTarget.Normalized();

        // Primary straight hop.
        var best = ResolveHop(from, primaryDir, hopDistance, radius);
        var bestProgress = ProgressToward(from, best, primaryDir);

        // Clear-direction fan: only if the straight hop slid to near-zero progress. Take the FIRST fan candidate that
        // makes positive (>= epsilon) progress, in the fixed ±45°/±90° order (deterministic).
        if (bestProgress < ProgressEpsilonUnits)
        {
            foreach (var rot in FanRotations)
            {
                var dir = Rotate(primaryDir, rot);
                var land = ResolveHop(from, dir, hopDistance, radius);
                var progress = ProgressToward(from, land, primaryDir);
                if (progress >= ProgressEpsilonUnits)
                {
                    best = land;
                    bestProgress = progress;
                    primaryDir = dir; // face the direction actually taken.
                    break;
                }
            }
        }

        if (bestProgress < ProgressEpsilonUnits)
        {
            // Wedged: neither the straight hop nor any fan candidate advances. Do NOT commit a move, do NOT arm the
            // cadence (re-try next tick), and report Stuck so the AI's watchdog counts a no-progress tick. Facing is
            // left as-is (a wedged monster shouldn't spin its sprite each tick).
            return HopResult.Stuck;
        }

        // MOVEMENT-ACTIONS (Phase C): commit the winning hop as a REAL ballistic Jump through the shared executor. The
        // locomotion's job is now to DECIDE the hop — the heading (primaryDir, possibly a fan rotation) and the clamped
        // distance that makes >= epsilon progress — and hand the actual movement to the executor, which arcs the XY
        // (the SAME shared resolver + walls + radius this method just probed) and the ballistic Z per tick. `best`/
        // `bestProgress` above are the DECISION probe (Moved vs Stuck, which heading); the executor re-resolves the arc
        // per tick to physically land the monster.
        //
        // ORDER IS LOAD-BEARING: begin the executor hop BEFORE arming the cadence. WorldEntity.IsHopReady and
        // IsMovementFrozen read the SAME `_nextEligibleTick` field and are exact complements (frozen == !ready); the
        // executor's CanStart REJECTS a movement-frozen entity. We are past the IsHopReady gate, so the monster is NOT
        // frozen RIGHT NOW — but TryBeginHop arms `_nextEligibleTick` into the FUTURE, which would make it look frozen.
        // So if we armed first, executor.TryStart would see IsMovementFrozen → reject the very hop we just decided.
        // Begin first (while still un-frozen), then arm. A (given the IsActive gate, theoretically impossible) rejected
        // trigger leaves the cadence un-armed and reports Stuck (no-progress) rather than arming a phantom hop.
        if (!_beginHop(monster, primaryDir, hopDistance, cooldownTicks, serverTick))
        {
            return HopResult.Stuck;
        }

        // Arm the cadence (an accepted hop, TryStep's arm rule) and face the heading. IsHopReady was true, so
        // TryBeginHop accepts and arms serverTick + cooldown — the next hop is gated until this arc's window elapses
        // (the executor's IsActive gate above covers the in-flight ticks; the cadence covers the seam after it lands).
        monster.TryBeginHop(serverTick, cooldownTicks);
        monster.SetFacingFromUnit(primaryDir);
        return HopResult.Moved;
    }

    // Resolve ONE candidate hop of `hopDistance` in `unitDir` from `from` against the nearby walls (slide/stop,
    // anti-tunnel). Pure w.r.t. the entity — does NOT mutate it; the caller applies the winning landing.
    private WorldVector ResolveHop(WorldVector from, WorldVector unitDir, double hopDistance, double radius)
    {
        var delta = unitDir * hopDistance;
        _queryWalls(from, delta, radius, _wallScratch);
        return ContinuousCollision.Resolve(from, delta, radius, _wallScratch);
    }

    // Signed progress (tile units) of a resolved landing toward the ORIGINAL target heading: the landing displacement
    // projected onto the primary unit direction. A slide along a perpendicular wall projects to ~0; a straight clear
    // hop projects to ~HopDistance. Using the projection (not raw distance moved) means a sideways slide that doesn't
    // actually approach the target is correctly counted as no-progress, which is what the fan/watchdog need.
    private static double ProgressToward(WorldVector from, WorldVector landing, WorldVector primaryDir)
        => (landing - from).Dot(primaryDir);

    // Rotate a unit vector by `radians` (CCW in the XY plane). Used to build the fan headings off the primary.
    private static WorldVector Rotate(WorldVector v, double radians)
    {
        var cos = System.Math.Cos(radians);
        var sin = System.Math.Sin(radians);
        return new WorldVector((v.X * cos) - (v.Y * sin), (v.X * sin) + (v.Y * cos));
    }

    private static double Deg(double degrees) => degrees * System.Math.PI / 180d;
}
