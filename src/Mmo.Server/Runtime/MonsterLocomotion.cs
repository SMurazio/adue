using Mmo.Shared.Domain;

namespace Mmo.Server.Runtime;

// CONTINUOUS MIGRATION (Phase 8): the monster MOVEMENT-STYLE seam. The roam/chase AI (IMonsterBehavior) decides WHERE a
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

    // MONSTER-BEHAVIOR P2 (docs/monster-behavior-design.md): the AI calls Stop the instant a monster transitions to
    // NOT moving (arrival → Idle, a wedge bail, an in-range stop-to-attack, entering Idle), so a VELOCITY-based body
    // (GlideLocomotion) zeroes its replicated Velocity exactly at the stop edge — the client then stops extrapolating
    // cleanly at the final position instead of coasting past it. It is a NO-OP for a cadence-gated body whose Velocity
    // is already Zero (HopLocomotion), so the slime is unchanged by the Stop wiring. The AI places this call ONLY at
    // genuine stop edges — never while a monster is still gliding toward a live target — so a moving glider keeps its
    // velocity until it actually arrives/bails.
    void Stop(WorldEntity monster);
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
    private readonly QueryObstaclesDelegate? _queryObstacles;
    private readonly BeginHopDelegate _beginHop;
    private readonly Func<ulong, bool> _isActionActive;
    private readonly List<ContinuousCollision.Wall> _wallScratch = new();
    private readonly List<ContinuousCollision.Circle> _obstacleScratch = new();

    // Fills `scratch` with the collision walls near a swept move (from, delta, radius) in stable row-major order — the
    // SAME TileGrid.QueryNearbyWalls / TileWalls helper the player integrator uses, injected so the locomotion is unit-
    // testable against a bare TileGrid without a live Zone.
    public delegate void QueryWallsDelegate(
        WorldVector start, WorldVector delta, double radius, List<ContinuousCollision.Wall> scratch);

    // PLAYER↔MONSTER COLLISION: fills `scratch` with the nearby PLAYER bodies (as Circles of the body radius) a monster
    // move should collide against, so a chasing monster STOPS at the player instead of overlapping it (monster↔monster
    // stays the separation pass's job). Injected exactly like the wall query (so the locomotion is unit-testable without
    // a live Zone) and OPTIONAL — null ⇒ no body obstacles (the tests' open-field behaviour, byte-identical to before).
    // Shared by HopLocomotion + GlideLocomotion (and mirrors ContinuousCollision.Circle's stable-order contract).
    public delegate void QueryObstaclesDelegate(
        WorldVector start, WorldVector delta, double radius, List<ContinuousCollision.Circle> scratch);

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
        Func<ulong, bool> isActionActive,
        QueryObstaclesDelegate? queryObstacles = null)
    {
        _hopDistanceUnits = hopDistanceUnits;
        _bodyRadiusUnits = bodyRadiusUnits;
        _queryWalls = queryWalls;
        _beginHop = beginHop;
        _isActionActive = isActionActive;
        _queryObstacles = queryObstacles;
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

        // PLAYER↔MONSTER COLLISION: snapshot the nearby PLAYER obstacle set ONCE per Advance (consistent geometry across
        // the primary hop + the whole fan, like the radius/hopDistance snapshot) so the hop DECISION (Moved/Stuck, which
        // heading) accounts for a player in the way — the monster won't decide to hop straight through a player. Null
        // gather ⇒ empty set (the open-field tests, unchanged). The PHYSICAL stop is the executor's job (it re-resolves
        // the arc per tick against the SAME player set — see GameServer.BeginMonsterHop / ServerActionExecutor).
        GatherObstacles(from, primaryDir * hopDistance, radius);

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

    // MONSTER-BEHAVIOR P2: a NO-OP for the hop. A hopper carries Velocity Zero throughout (it stays OFF the velocity-
    // glide path — the leap is a discrete collision-resolved apply, not a velocity integration), so there is nothing
    // to stop when the AI transitions it out of a moving phase. The slime's behavior is therefore unchanged by the
    // AI's new Stop calls (this is the contract that keeps every existing slime/monster test green).
    public void Stop(WorldEntity monster)
    {
    }

    // Resolve ONE candidate hop of `hopDistance` in `unitDir` from `from` against the nearby walls (slide/stop,
    // anti-tunnel). Pure w.r.t. the entity — does NOT mutate it; the caller applies the winning landing.
    private WorldVector ResolveHop(WorldVector from, WorldVector unitDir, double hopDistance, double radius)
    {
        var delta = unitDir * hopDistance;
        _queryWalls(from, delta, radius, _wallScratch);
        return _obstacleScratch.Count == 0
            ? ContinuousCollision.Resolve(from, delta, radius, _wallScratch)
            : ContinuousCollision.Resolve(from, delta, radius, _wallScratch, _obstacleScratch);
    }

    // PLAYER↔MONSTER COLLISION: refill `_obstacleScratch` (cleared) with the nearby player bodies for THIS Advance via
    // the injected gather. No gather injected (the unit tests) ⇒ an empty set ⇒ ResolveHop takes the walls-only path,
    // byte-identical to before.
    private void GatherObstacles(WorldVector from, WorldVector delta, double radius)
    {
        _obstacleScratch.Clear();
        _queryObstacles?.Invoke(from, delta, radius, _obstacleScratch);
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

// MONSTER-BEHAVIOR P2 (docs/monster-behavior-design.md): the CONTINUOUS-WALK locomotion — the FIRST visible behavior
// difference (a monster that WALKS instead of hops). Unlike HopLocomotion (a discrete, cadence-gated leap with Velocity
// left at Zero), a glider moves EVERY tick: it integrates a small step along the heading at its walk speed
// (WorldEntity.SpeedUnitsPerSecond, seeded from the type's MoveSpeedMultiplier at spawn), through the SAME shared swept-
// circle collision the player + the hop use (the resolver SLIDES along walls, so a glider follows a wall with NO fan —
// the resolver does the local steering), and SETS its replicated Velocity = heading × speed.
//
// REPLICATION (no protocol change): Velocity is already on the wire (v39) and a moving entity (Velocity != 0) is force-
// included every tick + the DEFAULT remote render EXTRAPOLATES along that velocity. So a glider that SETS Velocity
// replicates + extrapolates smoothly with NO protocol/wire change — it just USES the velocity the hop leaves at Zero.
//
// One Advance (every tick — NOT cadence-gated, so it ignores cooldownTicks for gating and NEVER returns OnCooldown):
//   1. toTarget = target − from; if on target (<= epsilon) → Stuck (a guard; the AI checks arrival/adjacency first).
//   2. dir = toTarget.Normalized(); ComputeMoveDelta(dir, dt) SETS Velocity = dir×speed + Facing and returns the raw
//      per-tick delta (dir×speed×dt). CLAMP the delta length to toTarget.Length so the final approach never overshoots
//      a near target (mirrors the hop's clamp; without it a fixed step would oscillate across a closer-than-one-step
//      destination forever and never land within the arrival epsilon).
//   3. resolve the (clamped) delta through QueryWalls + ContinuousCollision.Resolve at the body radius — collision-VALID
//      (the SAME wall derivation + radius players collide at), sliding/stopping at walls.
//   4. apply the resolved landing via the injected apply-landing delegate (Zone.ApplyMonsterLanding — the SAME tile-
//      crossing bookkeeping + spatial-bucket migration the hop lands through).
//   5. progress = resolved displacement projected onto dir (HopLocomotion.ProgressToward's notion). >= ProgressEpsilon
//      ⇒ Moved (the AI marks progress / resets its watchdog); else ⇒ Stuck (a wall the resolver can't slide past — a
//      genuine wedge, which the AI's no-progress watchdog bails). A moving glider advances every tick, so it never
//      FALSELY trips the watchdog; only a truly wedged one (Stuck every tick) does.
//
// COOLDOWN WART (minor, noted in the design): cooldownTicks is passed in (derived from the type's hop knobs even for a
// glider) and is used ONLY as the AI's no-progress-watchdog TIMEOUT window — glide has no real cadence. Fine for P2; a
// cleaner per-locomotion cadence is a later refinement.
//
// DETERMINISM: all-double, RNG-free, fixed dt (1/tickRate) + the stable shared wall query — a given (from, target,
// speed, walls) yields a byte-identical landing every call (the same contract the hop + the player integrator hold).
public sealed class GlideLocomotion : IMonsterLocomotion
{
    // Apply a glider's resolved per-tick landing — WorldEntity.ApplyResolvedMove + spatial-bucket migration on a tile
    // cross — the SAME Zone.ApplyMonsterLanding seam the hop lands through. Injected so the locomotion is unit-testable
    // against a bare TileGrid + WorldState without a live Zone.
    public delegate bool ApplyLandingDelegate(WorldEntity monster, WorldVector landing);

    // Body radius is read FRESH per Advance (a live "continuous.bodyRadius" retune applies on the next tick — same as
    // the hop + the player integrator), pinned to the player radius so a glider fits exactly where a player does.
    private readonly Func<double> _bodyRadiusUnits;
    private readonly HopLocomotion.QueryWallsDelegate _queryWalls;
    private readonly HopLocomotion.QueryObstaclesDelegate? _queryObstacles;
    private readonly ApplyLandingDelegate _applyLanding;
    private readonly Func<WorldEntity, bool> _isActionActive;
    private readonly double _dtSeconds;
    private readonly List<ContinuousCollision.Wall> _wallScratch = new();
    private readonly List<ContinuousCollision.Circle> _obstacleScratch = new();

    // tickRate fixes the per-tick integration step dt = 1/tickRate (the SAME fixed server tick the player integrator
    // and the ballistic Z use; the server owns speed AND dt, so anti-speedhack is intrinsic just like a player move).
    // MONSTER-BEHAVIOR P5: `isActionActive` is the SAME self-guard HopLocomotion carries (GameServer passes
    // `m => _actionExecutor.IsActive(m)`) — while a shared-executor action (the gnoll's charge dash) owns the monster's
    // movement, the glide must NOT also step it (a DOUBLE-MOVE), exactly as the hop self-guards while its arc is in flight.
    public GlideLocomotion(
        Func<double> bodyRadiusUnits,
        HopLocomotion.QueryWallsDelegate queryWalls,
        ApplyLandingDelegate applyLanding,
        int tickRate,
        Func<WorldEntity, bool> isActionActive,
        HopLocomotion.QueryObstaclesDelegate? queryObstacles = null)
    {
        _bodyRadiusUnits = bodyRadiusUnits;
        _queryWalls = queryWalls;
        _applyLanding = applyLanding;
        _isActionActive = isActionActive;
        _queryObstacles = queryObstacles;
        _dtSeconds = tickRate > 0 ? 1d / tickRate : 0d;
    }

    public HopResult Advance(WorldEntity monster, WorldVector target, uint serverTick, uint cooldownTicks)
    {
        // MONSTER-BEHAVIOR P5: a shared-executor action (a CHARGE dash) is in flight — the executor owns the monster's
        // movement mid-action. StepMonsterAi runs BEFORE the executor's StepAll, so without this gate a charging gnoll
        // would DOUBLE-MOVE (executor dash + a glide step the SAME tick). Mirror HopLocomotion EXACTLY: return OnCooldown
        // (a harmless cadence wait) WITHOUT moving and WITHOUT touching Velocity — so the no-progress watchdog is NOT
        // tripped (the monster IS making progress along the dash, force-included densely per tick like the hop). The
        // slime is unaffected: it uses HopLocomotion, and a glider that never charges never sees an active action here.
        if (_isActionActive(monster))
        {
            return HopResult.OnCooldown;
        }

        var from = monster.Position;
        var toTarget = target - from;
        if (toTarget.LengthSquared <= 0d)
        {
            // Already on the target (the AI checks arrival/adjacency first, so this is a guard). No move.
            return HopResult.Stuck;
        }

        var radius = _bodyRadiusUnits();
        var dir = toTarget.Normalized();

        // ComputeMoveDelta sets the DESIRED Velocity = dir × SpeedUnitsPerSecond + Facing and returns the raw per-tick
        // delta (dir × speed × dt). Reused for the shared velocity/facing rule. CLAMP to the remaining distance so the
        // final approach never overshoots the target.
        var rawDelta = monster.ComputeMoveDelta(dir, _dtSeconds);
        var delta = rawDelta.Length > toTarget.Length ? dir * toTarget.Length : rawDelta;

        // Resolve the step through the SAME shared collision the player + the hop use — the resolver SLIDES along walls
        // (a glider follows a wall, no fan needed), so a blocked step yields a sideways landing or a fixpoint.
        // PLAYER↔MONSTER COLLISION: also gather the nearby PLAYER bodies and resolve against them, so a chasing glider
        // STOPS at / slides along the player instead of overlapping it (server-only — a monster isn't predicted). No
        // gather injected (the unit tests) ⇒ empty set ⇒ the walls-only path, byte-identical to before.
        _queryWalls(from, delta, radius, _wallScratch);
        _obstacleScratch.Clear();
        _queryObstacles?.Invoke(from, delta, radius, _obstacleScratch);
        var landing = _obstacleScratch.Count == 0
            ? ContinuousCollision.Resolve(from, delta, radius, _wallScratch)
            : ContinuousCollision.Resolve(from, delta, radius, _wallScratch, _obstacleScratch);
        _applyLanding(monster, landing);

        // VELOCITY COHERENCE (the replication guardrail): replicate the ACTUAL resolved motion, NOT the pre-collision
        // desired dir×speed. Facing keeps the intended dir (set by ComputeMoveDelta) so the sprite still faces the target.
        var resolvedDelta = landing - from;
        if (resolvedDelta.LengthSquared > 0d)
        {
            // Moving / sliding: Velocity = the resolved per-tick velocity, so a glider following a wall replicates its
            // TANGENTIAL slide velocity (non-zero ⇒ force-included every tick) and the client extrapolation tracks the
            // real path instead of drifting into the wall.
            monster.SetVelocity(resolvedDelta * (1d / _dtSeconds));
        }
        else
        {
            // WEDGED (resolved motion exactly 0 — boxed head-on / inside corner): use StopMovement, NOT a bare
            // SetVelocity(0). A bare zero leaves Velocity == 0 (so forceMoving is false) WITHOUT a StateRevision bump,
            // so the entity is delta'd out and the client keeps extrapolating the PRE-wedge velocity INTO the wall until
            // the no-progress watchdog finally bails (the P2-review HIGH). StopMovement fires the stop-edge revision
            // bump → Velocity=0 re-publishes THIS tick → the client holds at the contact point, no drift.
            monster.StopMovement();
        }

        // Progress = the resolved displacement projected onto the target heading (like Hop's ProgressToward). A real
        // walk projects ~|delta|; a slide along a perpendicular wall projects ~0. >= epsilon ⇒ Moved; else Stuck (a
        // wall the resolver can't slide past — the AI's no-progress watchdog bails it). NEVER OnCooldown (no cadence).
        var progress = (landing - from).Dot(dir);
        return progress >= HopLocomotion.ProgressEpsilonUnits ? HopResult.Moved : HopResult.Stuck;
    }

    // MONSTER-BEHAVIOR P2: zero the glider's replicated Velocity (StopMovement also bumps StateRevision ONCE on the
    // moving→stopped transition, the stop-edge re-publish), so the client stops extrapolating cleanly at the final
    // position. Called by the AI exactly at the stop edges (arrival, wedge bail, in-range stop-to-attack, Idle).
    public void Stop(WorldEntity monster) => monster.StopMovement();
}
