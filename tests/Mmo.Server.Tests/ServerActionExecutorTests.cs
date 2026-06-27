using Mmo.Server.Runtime;
using Mmo.Shared.Domain;
using Mmo.Shared.Domain.Actions;
using Xunit;

namespace Mmo.Server.Tests;

// MOVEMENT-ACTIONS (Phase A): headless tests for the server-side ballistic-jump executor (design §6 Phase A gate).
// They pin BOTH the XY per-tick path AND the Z trajectory (apex height, landing tick, ground-snap), the "jump into a
// wall -> land short" case (XY wall-blocked while Z still completes + snaps), the cooldown re-trigger reject, the
// InPlace jump (XY stays, Z arcs), and DETERMINISM (an identical trigger yields a byte-identical path — the contract
// Phase B's prediction depends on). NO netcode here — Phase A is server-side only and the executor is unit-triggerable.
//
// The executor is wired with the SAME shared collision derivation ordinary movement uses (TileGrid.QueryNearbyWalls +
// WorldEntity.ApplyResolvedMove), so a jump collides byte-identically to a walk. Body radius is the default 0.5.
public sealed class ServerActionExecutorTests
{
    private const int TickRate = 20;
    private const double Radius = CollisionDefaults.BodyRadius; // 0.5
    private const double Eps = 1e-9;

    // Build an executor over a TileGrid (so the wall test uses the REAL shared derivation) plus a bare WorldEntity.
    private static (ServerActionExecutor executor, WorldEntity entity) Build(
        TileCoord spawn, TileCoord[]? blocked = null, double speed = 5d)
    {
        var grid = new TileGrid(64, 64, blocked ?? System.Array.Empty<TileCoord>());
        var executor = new ServerActionExecutor(
            TickRate,
            () => Radius,
            grid.QueryNearbyWalls,
            (entity, resolved) => entity.ApplyResolvedMove(resolved));

        var ent = new WorldEntity(
            id: 1,
            networkId: 1,
            EntityKind.Player,
            spawn,
            Direction8.S,
            "Player1",
            System.Guid.NewGuid(),
            ownerSession: null,
            isDurable: true);
        ent.SetSpeedUnitsPerSecond(speed);
        return (executor, ent);
    }

    // Drive an action to completion: trigger, then Step until it ends (or a safety cap). Returns the per-tick height
    // samples (index i = VerticalOffset AFTER Step i, with index 0 = the post-trigger takeoff value).
    private static System.Collections.Generic.List<double> RunToCompletion(
        ServerActionExecutor executor, WorldEntity entity, MovementActionDef def, WorldVector heading, uint serverTick)
    {
        var heights = new System.Collections.Generic.List<double>();
        Assert.True(executor.TryStart(entity, def, heading, serverTick));
        heights.Add(entity.VerticalOffset); // tick 0 (takeoff)

        var tick = serverTick;
        for (var i = 0; i < def.DurationTicks + 4; i++)
        {
            tick++;
            var stillActive = executor.Step(entity, tick);
            heights.Add(entity.VerticalOffset);
            if (!stillActive)
            {
                break;
            }
        }

        return heights;
    }

    [Fact]
    public void ForwardArcJump_ReachesForwardTarget_AlongTheArc()
    {
        // Open ground, jump EAST. The un-collided forward arc advances the full ForwardDistanceUnits along the locked
        // heading over DurationTicks; with no wall it lands at origin + distance, exactly.
        var (executor, entity) = Build(spawn: new TileCoord(8, 8));
        var def = MovementActionRegistry.BuildForwardArcJump(
            ActionId.Jump, durationTicks: 10, jumpHeight: 2d, forwardDistanceUnits: 5d, cooldownTicks: 0, animationId: 1);
        var origin = entity.Position;

        // Track XY monotonic progress east along the way (the arc advances, never retreats, each tick).
        Assert.True(executor.TryStart(entity, def, Direction8.E.ToUnitVector(), serverTick: 100));
        var prevX = entity.Position.X;
        for (var i = 0; i < 10; i++)
        {
            executor.Step(entity, (uint)(101 + i));
            Assert.True(entity.Position.X >= prevX - Eps, "the forward arc retreated");
            prevX = entity.Position.X;
        }

        Assert.Equal(origin.X + 5d, entity.Position.X, 1e-6); // reached the forward target
        Assert.Equal(origin.Y, entity.Position.Y, 1e-6);      // no lateral drift
        Assert.False(executor.IsActive(entity));              // ended on the final tick
    }

    [Fact]
    public void BallisticZ_ApexAtMidDuration_AndLandsToExactlyZeroAtTickN()
    {
        // The Z arc peaks at the midpoint tick (N/2) at exactly JumpHeight, and VerticalOffset returns to EXACTLY 0 at
        // tick N (the explicit ground-snap — no float-seam drift).
        var (executor, entity) = Build(spawn: new TileCoord(8, 8));
        const uint n = 10;
        const double h = 2d;
        var def = MovementActionRegistry.BuildForwardArcJump(
            ActionId.Jump, durationTicks: n, jumpHeight: h, forwardDistanceUnits: 5d, cooldownTicks: 0, animationId: 1);

        var heights = RunToCompletion(executor, entity, def, Direction8.E.ToUnitVector(), serverTick: 100);

        // heights[0] = takeoff (0), heights[i] = height after Step i. Apex at i = N/2.
        Assert.Equal(0d, heights[0], Eps);                 // takeoff is grounded
        Assert.Equal(h, heights[(int)(n / 2)], 1e-9);      // apex == JumpHeight at the midpoint tick
        Assert.Equal(0d, heights[(int)n], Eps);            // EXACTLY 0 at tick N (the snap)
        Assert.True(entity.VerticalOffset == 0d, "did not land to exactly 0");

        // The arc is airborne (>0) strictly between takeoff and landing.
        for (var i = 1; i < (int)n; i++)
        {
            Assert.True(heights[i] > 0d, $"tick {i} was not airborne (z={heights[i]})");
        }
    }

    [Fact]
    public void JumpIntoWall_LandsShort_ButZStillCompletesAndSnaps()
    {
        // Block (12,8) directly east of the (8,8) spawn. A forward-arc jump EAST is wall-blocked in XY (it cannot enter
        // the blocked tile — stops at the -X face minus radius => x = 11.5 - 0.5 = 11.0), so it LANDS SHORT of the full
        // 5-unit reach. But Z is FREE: it still arcs to apex and snaps to exactly 0 on the landing tick.
        var (executor, entity) = Build(spawn: new TileCoord(8, 8), blocked: new[] { new TileCoord(12, 8) });
        var def = MovementActionRegistry.BuildForwardArcJump(
            ActionId.Jump, durationTicks: 10, jumpHeight: 2d, forwardDistanceUnits: 5d, cooldownTicks: 0, animationId: 1);
        var origin = entity.Position;

        var heights = RunToCompletion(executor, entity, def, Direction8.E.ToUnitVector(), serverTick: 100);

        // XY landed SHORT: wall-blocked at x = 11.0, never reaching origin.X + 5 = 13.0, and never entering the wall.
        Assert.True(entity.Position.X < origin.X + 5d - Eps, $"did not land short: x={entity.Position.X}");
        Assert.Equal(11.0d, entity.Position.X, 1e-6);
        Assert.NotEqual(new TileCoord(12, 8), entity.TileCoord);

        // Z still completed: apex hit JumpHeight and the final offset snapped to exactly 0.
        Assert.Equal(2d, heights[5], 1e-9);
        Assert.Equal(0d, entity.VerticalOffset, Eps);
        Assert.False(executor.IsActive(entity));
    }

    [Fact]
    public void Cooldown_RejectsReTrigger_BeforeItElapses()
    {
        // A non-zero cooldown arms when the action ENDS; a re-trigger within the window is rejected, and accepted once
        // the cooldown elapses.
        var (executor, entity) = Build(spawn: new TileCoord(8, 8));
        const uint duration = 6;
        const uint cooldown = 10;
        var def = MovementActionRegistry.BuildForwardArcJump(
            ActionId.Jump, durationTicks: duration, jumpHeight: 1d, forwardDistanceUnits: 2d, cooldownTicks: cooldown, animationId: 1);

        Assert.True(executor.TryStart(entity, def, Direction8.E.ToUnitVector(), serverTick: 100));
        // Can't trigger while already active (one-at-a-time).
        Assert.False(executor.TryStart(entity, def, Direction8.E.ToUnitVector(), serverTick: 101));

        uint endTick = 0;
        for (var t = 101u; t <= 100u + duration; t++)
        {
            if (!executor.Step(entity, t))
            {
                endTick = t;
                break;
            }
        }

        Assert.False(executor.IsActive(entity));
        // The cooldown was armed at endTick; a re-trigger inside [endTick, endTick + cooldown) is rejected.
        Assert.False(executor.TryStart(entity, def, Direction8.E.ToUnitVector(), serverTick: endTick + 1));
        Assert.False(executor.TryStart(entity, def, Direction8.E.ToUnitVector(), serverTick: endTick + cooldown - 1));
        // Once the cooldown elapses, a re-trigger is accepted.
        Assert.True(executor.TryStart(entity, def, Direction8.E.ToUnitVector(), serverTick: endTick + cooldown));
    }

    [Fact]
    public void InPlaceJump_XYStays_ZArcs()
    {
        // An InPlace jump: XY holds at Origin (straight up + down), Z still arcs to apex and lands to 0.
        var (executor, entity) = Build(spawn: new TileCoord(8, 8));
        var def = MovementActionRegistry.BuildInPlaceJump(
            ActionId.Jump, durationTicks: 10, jumpHeight: 1.5d, cooldownTicks: 0, animationId: 1);
        var origin = entity.Position;

        var heights = RunToCompletion(executor, entity, def, Direction8.E.ToUnitVector(), serverTick: 100);

        Assert.Equal(origin.X, entity.Position.X, Eps); // XY never moved
        Assert.Equal(origin.Y, entity.Position.Y, Eps);
        Assert.Equal(1.5d, heights[5], 1e-9);           // apex == JumpHeight
        Assert.Equal(0d, entity.VerticalOffset, Eps);   // landed to 0
    }

    [Fact]
    public void MovementRootedEntity_CannotStartAnAction_NoRootEscapeByJumping()
    {
        // can-act gap (design §2.1 "not rooted"): a swing-movement-rooted player must NOT be able to jump to relocate
        // via the executor during the root window — else jumping ESCAPES the attack-root (which only gates the ordinary
        // move integrator, not the executor). ApplyAttackMovementRoot freezes the entity; CanStart/TryStart must reject
        // until it elapses. No headless test triggered an action on a rooted entity before this (unbiased-review find).
        var (executor, entity) = Build(spawn: new TileCoord(8, 8));
        var def = MovementActionRegistry.BuildForwardArcJump(
            ActionId.Jump, durationTicks: 10, jumpHeight: 2d, forwardDistanceUnits: 5d, cooldownTicks: 0, animationId: 1);

        entity.ApplyAttackMovementRoot(serverTick: 100, rootTicks: 20); // frozen for ticks [100, 120)

        // Inside the root window: rejected, and nothing starts.
        Assert.False(executor.CanStart(entity, def, serverTick: 105));
        Assert.False(executor.TryStart(entity, def, Direction8.E.ToUnitVector(), serverTick: 105));
        Assert.False(executor.IsActive(entity));

        // Once the root elapses (serverTick >= rootUntil): allowed.
        Assert.True(executor.CanStart(entity, def, serverTick: 120));
        Assert.True(executor.TryStart(entity, def, Direction8.E.ToUnitVector(), serverTick: 120));
        Assert.True(executor.IsActive(entity));
    }

    [Fact]
    public void Landing_BumpsStateRevision_SoTheGroundedHeightReplicates()
    {
        // B1 LIVE SYMPTOM (the user saw the avatar left floating "a bit" after a jump, worst from a standstill): on the
        // landing tick the action ENDS (IsActive→false) the same tick VerticalOffset snaps to 0, and a jump's Velocity
        // is 0 — so the entity is neither force-included nor (before the fix) revision-bumped, and the grounded
        // VerticalOffset=0 never replicated → the client kept the last airborne height. An InPlace jump moves no XY, so
        // there is NO tile-cross StateRevision bump to mask the effect: the ONLY thing that can re-publish the landing
        // is SnapToGround's own bump. This pins the fix.
        var (executor, entity) = Build(spawn: new TileCoord(8, 8));
        const uint n = 10;
        var def = MovementActionRegistry.BuildInPlaceJump(
            ActionId.Jump, durationTicks: n, jumpHeight: 1.5d, cooldownTicks: 0, animationId: 1);

        Assert.True(executor.TryStart(entity, def, Direction8.E.ToUnitVector(), serverTick: 100));

        // Advance to the tick JUST BEFORE landing (still airborne). No XY move (InPlace) ⇒ StateRevision is quiescent.
        var tick = 100u;
        for (var i = 0; i < (int)n - 1; i++)
        {
            tick++;
            Assert.True(executor.Step(entity, tick)); // still active
        }
        Assert.True(entity.VerticalOffset > 0d, "should still be airborne before the final tick");
        var revisionBeforeLanding = entity.StateRevision;

        // The final (landing) tick: Z snaps to exactly 0 AND StateRevision bumps so a delta-snapshot re-includes the
        // entity and the client lands cleanly (the residual-float fix).
        tick++;
        Assert.False(executor.Step(entity, tick)); // ended this tick
        Assert.Equal(0d, entity.VerticalOffset, Eps);
        Assert.True(
            entity.StateRevision > revisionBeforeLanding,
            "landing must bump StateRevision so the grounded VerticalOffset replicates (else the client keeps a residual airborne height)");
    }

    [Fact]
    public void Determinism_IdenticalTrigger_YieldsByteIdenticalPath()
    {
        // The Phase-B prediction contract: two independent executors+entities, the same trigger + the same per-tick
        // Step stream, must produce a BYTE-IDENTICAL XY and Z path the whole way. (Bit-compared, not epsilon.)
        var blocked = new[] { new TileCoord(12, 8) }; // include a wall so the collided path is in the contract too
        var (execA, a) = Build(spawn: new TileCoord(8, 8), blocked: blocked);
        var (execB, b) = Build(spawn: new TileCoord(8, 8), blocked: blocked);
        var def = MovementActionRegistry.BuildForwardArcJump(
            ActionId.Jump, durationTicks: 12, jumpHeight: 2.25d, forwardDistanceUnits: 6d, cooldownTicks: 0, animationId: 1);

        var heading = Direction8.E.ToUnitVector();
        Assert.True(execA.TryStart(a, def, heading, serverTick: 100));
        Assert.True(execB.TryStart(b, def, heading, serverTick: 100));
        AssertBitIdentical(a, b);

        for (var t = 101u; t <= 116u; t++)
        {
            execA.Step(a, t);
            execB.Step(b, t);
            AssertBitIdentical(a, b);
        }
    }

    private static void AssertBitIdentical(WorldEntity a, WorldEntity b)
    {
        Assert.Equal(System.BitConverter.DoubleToInt64Bits(a.Position.X), System.BitConverter.DoubleToInt64Bits(b.Position.X));
        Assert.Equal(System.BitConverter.DoubleToInt64Bits(a.Position.Y), System.BitConverter.DoubleToInt64Bits(b.Position.Y));
        Assert.Equal(System.BitConverter.DoubleToInt64Bits(a.VerticalOffset), System.BitConverter.DoubleToInt64Bits(b.VerticalOffset));
    }
}
