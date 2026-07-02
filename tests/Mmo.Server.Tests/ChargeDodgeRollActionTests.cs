using Mmo.Server.Runtime;
using Mmo.Shared.Domain;
using Mmo.Shared.Domain.Actions;
using Xunit;

namespace Mmo.Server.Tests;

// MOVEMENT-ACTIONS (Phase D): headless tests for the two new player actions — CHARGE (a fast grounded forward dash)
// and DODGE-ROLL (a short grounded dash with a server-authoritative i-frame window) — mirroring the Phase-A/B
// executor suite (ServerActionExecutorTests). They pin: the SHIPPED registry def shapes; the open-ground reach; the
// charge-into-wall EARLY-STOP (motion pins at the wall face, deterministically, while the short instance runs out its
// ticks — the P5 gnoll-charge SlideStop model); the charge-into-body early-stop (the Phase-D player-actor obstacle
// gather); DETERMINISM (an identical trigger yields a byte-identical path, wall in the way, bit-compared); the
// grounded-Z invariant (a dash NEVER leaves the ground — VerticalOffset stays exactly 0 every tick); and the i-frame
// window semantics HasActiveIFrames exposes to the damage seam (inside negates, outside lands, cancel/end clears,
// jump/charge never have one). NO netcode here — the wire-order tests live in ActionIntentHandlerTests and the
// client-vs-server prediction gate in ContinuousPredictorChargeDodgeTests.
public sealed class ChargeDodgeRollActionTests
{
    private const int TickRate = 20;
    private const double Radius = CollisionDefaults.BodyRadius; // 0.5
    private const double Eps = 1e-9;

    private static MovementActionDef ChargeDef => MovementActionRegistry.Default.Get(ActionId.Charge);
    private static MovementActionDef DodgeRollDef => MovementActionRegistry.Default.Get(ActionId.DodgeRoll);

    // Build an executor over a TileGrid (the REAL shared wall derivation) plus a bare player WorldEntity, with an
    // optional body-obstacle set — mirroring the Phase-D player-actor gather (a stationary body the dash must stop
    // at). Same wiring shape as ServerActionExecutorTests.Build + the hop-into-player obstacle test.
    private static (ServerActionExecutor executor, WorldEntity entity) Build(
        TileCoord spawn, TileCoord[]? blocked = null, WorldVector? obstacle = null, double speed = 5d)
    {
        var grid = new TileGrid(64, 64, blocked ?? System.Array.Empty<TileCoord>());

        // The Phase-D player-actor gather: a charging/rolling PLAYER collides with nearby bodies (the GameServer impl
        // forwards to the Zone walking gather; a single stationary body is its deterministic essence headlessly).
        ServerActionExecutor.QueryObstaclesDelegate? queryObstacles = null;
        if (obstacle is { } body)
        {
            queryObstacles = (_, _, _, radius, scratch) =>
            {
                scratch.Clear();
                scratch.Add(new ContinuousCollision.Circle(body.X, body.Y, radius));
            };
        }

        var executor = new ServerActionExecutor(
            TickRate,
            () => Radius,
            grid.QueryNearbyWalls,
            (entity, resolved) => entity.ApplyResolvedMove(resolved),
            queryObstacles);

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

    // Drive an action to completion, asserting the GROUNDED-Z invariant every tick (a dash never leaves the ground).
    private static void RunDashToCompletion(
        ServerActionExecutor executor, WorldEntity entity, MovementActionDef def, WorldVector heading, uint serverTick)
    {
        Assert.True(executor.TryStart(entity, def, heading, serverTick));
        Assert.Equal(0d, entity.VerticalOffset, Eps); // grounded at trigger

        var tick = serverTick;
        for (var i = 0; i < def.DurationTicks + 4; i++)
        {
            tick++;
            var stillActive = executor.Step(entity, tick);
            Assert.Equal(0d, entity.VerticalOffset, Eps); // grounded EVERY tick — no Z arc on a dash
            if (!stillActive)
            {
                break;
            }
        }

        Assert.False(executor.IsActive(entity));
    }

    // ACTION-END STOP-EDGE (todo/S-dash-end-replication-bump): the reviewer-constructed replication gap. A
    // STANDSTILL dash whose FINAL tick does not cross a rounded tile had NO re-publish path once the instance ended
    // (IsActive false, Velocity 0, Z unchanged, no tile cross) — remote viewers held the previous tick's position
    // indefinitely. EndInstance now bumps StateRevision unconditionally, so the delta gate (!HasAckedCurrentRevision)
    // re-includes the final position on the next snapshot. Pinned at the mechanism level: the revision MUST advance
    // across the final Step even when the final tick provably stays inside one rounded tile.
    [Fact]
    public void FlatDashEnd_BumpsStateRevision_EvenWithoutATileCross()
    {
        var (executor, ent) = Build(new TileCoord(8, 8));
        // Place the roller at x=7.6 (sub-tile, still tile 8): per-tick step 0.5u → 8.1, 8.6, 9.1, 9.6, 10.1. The
        // final tick moves 9.6 → 10.1: both round to tile 10 — the no-tile-cross final step the gap needs.
        ent.ApplyResolvedMove(new WorldVector(7.6, 8.0));

        Assert.True(executor.TryStart(ent, DodgeRollDef, new WorldVector(1, 0), serverTick: 100));
        uint tick = 100;
        for (var i = 1; i < DodgeRollDef.DurationTicks; i++)
        {
            tick++;
            Assert.True(executor.Step(ent, tick));
        }

        // At the penultimate tick: x=9.6 (tile 10). Capture the revision the viewer would have acked.
        Assert.Equal(9.6d, ent.Position.X, 6);
        Assert.Equal(new TileCoord(10, 8), ent.TileCoord);
        var ackedRevision = ent.StateRevision;

        // The final tick lands at 10.1 — SAME rounded tile, grounded Z, instance removed, Velocity 0.
        tick++;
        Assert.False(executor.Step(ent, tick));
        Assert.Equal(10.1d, ent.Position.X, 6);
        Assert.Equal(new TileCoord(10, 8), ent.TileCoord);
        Assert.False(executor.IsActive(ent));

        // The action-end stop-edge: the revision advanced, so the final sub-tile position re-publishes.
        Assert.True(ent.StateRevision > ackedRevision,
            "action end must bump StateRevision — otherwise the dash's final sub-tile step never replicates");
    }

    // The same stop-edge must fire on a CANCEL (an interrupt mid-dash leaves the entity at an arbitrary sub-tile
    // position with the instance gone — the identical delta'd-out exposure).
    [Fact]
    public void FlatDashCancel_BumpsStateRevision()
    {
        var (executor, ent) = Build(new TileCoord(8, 8));
        ent.ApplyResolvedMove(new WorldVector(7.6, 8.0));

        Assert.True(executor.TryStart(ent, DodgeRollDef, new WorldVector(1, 0), serverTick: 100));
        Assert.True(executor.Step(ent, 101)); // one tick in — x=8.1, still tile 8, no cross from 7.6
        var ackedRevision = ent.StateRevision;

        executor.Cancel(ent, 102);

        Assert.False(executor.IsActive(ent));
        Assert.True(ent.StateRevision > ackedRevision,
            "cancel must bump StateRevision — the interrupted position needs the same re-publish");
    }

    [Fact]
    public void Registry_ShipsTheTwoPlayerDashDefs_WithTheExpectedShape()
    {
        // Pin the SHIPPED def shapes (the numbers are feel placeholders, but the STRUCTURE is the contract): both are
        // GROUNDED (no Z arc), locked-heading, committed, cooled-down forward dashes; only the roll has an i-frame
        // window, and that window sits INSIDE the roll's active ticks with a vulnerable recovery tail.
        var charge = ChargeDef;
        Assert.Equal(0d, charge.JumpHeight, Eps);          // grounded — a dash, not a jump
        Assert.True(charge.ForwardDistanceUnits > 0d);     // a real forward reach
        Assert.True(charge.DurationTicks > 0);
        Assert.True(charge.CooldownTicks > 0);             // server-enforced re-trigger clock
        Assert.False(charge.CanSteer);                     // locked heading (design decision #4)
        Assert.False(charge.Interruptible);                // committed
        Assert.False(charge.HasIFrameWindow);              // a charge is a closer, not an evade

        var roll = DodgeRollDef;
        Assert.Equal(0d, roll.JumpHeight, Eps);
        Assert.True(roll.ForwardDistanceUnits > 0d);
        Assert.True(roll.ForwardDistanceUnits < charge.ForwardDistanceUnits); // an evade — shorter than the closer
        Assert.True(roll.CooldownTicks > 0);
        Assert.True(roll.HasIFrameWindow);                 // the whole point of the roll
        Assert.True(roll.IFrameStartTick >= 1);            // never invulnerable on the trigger tick itself
        Assert.True(roll.IFrameEndTick < roll.DurationTicks, "the roll must land vulnerable (a recovery tail)");

        // The jump def gained NO window from the Phase-D field addition (default-empty on the untouched def).
        Assert.False(MovementActionRegistry.Default.Get(ActionId.Jump).HasIFrameWindow);
    }

    [Fact]
    public void Charge_OpenGround_ReachesFullDistance_StaysGrounded()
    {
        var (executor, entity) = Build(spawn: new TileCoord(8, 8));
        var def = ChargeDef;
        var origin = entity.Position;

        RunDashToCompletion(executor, entity, def, Direction8.E.ToUnitVector(), serverTick: 100);

        Assert.Equal(origin.X + def.ForwardDistanceUnits, entity.Position.X, 1e-6); // full reach east
        Assert.Equal(origin.Y, entity.Position.Y, 1e-6);                            // no lateral drift
        Assert.Equal(Direction8.E, entity.Facing);                                  // faced + held the locked heading
    }

    [Fact]
    public void Charge_IntoWall_EarlyStopsAtTheWallFace_AndStaysPinnedForTheRemainingTicks()
    {
        // Block (11,8) two tiles east of the (8,8) spawn. The 4-unit charge would reach x=12; the wall face is at
        // x = 10.5 − 0.5(radius) = 10.0. The SlideStop model: the shared resolver pins the per-tick forward delta at
        // the face, so the MOTION early-stops there and the entity holds that exact position for the dash's remaining
        // ticks (the short instance still runs out its DurationTicks, like the P5 gnoll charge) — deterministically,
        // never entering the blocked tile.
        var (executor, entity) = Build(spawn: new TileCoord(8, 8), blocked: new[] { new TileCoord(11, 8) });
        var def = ChargeDef;

        Assert.True(executor.TryStart(entity, def, Direction8.E.ToUnitVector(), serverTick: 100));
        var pinnedTicks = 0;
        for (var t = 101u; t <= 100u + def.DurationTicks; t++)
        {
            var before = entity.Position.X;
            executor.Step(entity, t);
            Assert.True(entity.Position.X <= 10.0d + 1e-9, $"entered/passed the wall face at tick {t}: x={entity.Position.X}");
            if (System.Math.Abs(entity.Position.X - before) < 1e-12 && before >= 10.0d - 1e-6)
            {
                pinnedTicks++; // a post-contact tick: the resolver produced (essentially) zero motion at the face
            }
        }

        Assert.Equal(10.0d, entity.Position.X, 1e-6);       // stopped exactly at the wall face
        Assert.NotEqual(new TileCoord(11, 8), entity.TileCoord);
        Assert.True(pinnedTicks >= 1, "the dash should spend at least one tick pinned at the face (the early-stop)");
        Assert.False(executor.IsActive(entity));            // the instance ran out its ticks and ended
    }

    [Fact]
    public void Charge_IntoStationaryBody_StopsAtContact_NeverPassesThrough()
    {
        // A stationary body 2 tiles east (the Phase-D player-actor obstacle gather in play): the dash must stop at
        // centre distance 2×radius and never overlap or pass the body — the same contract the monster hop-into-player
        // test pins, now for the PLAYER dash.
        var body = new WorldVector(10d, 8d);
        var (executor, entity) = Build(spawn: new TileCoord(8, 8), obstacle: body);
        var def = ChargeDef;

        Assert.True(executor.TryStart(entity, def, Direction8.E.ToUnitVector(), serverTick: 100));
        for (var t = 101u; t <= 100u + def.DurationTicks; t++)
        {
            executor.Step(entity, t);
            var d = System.Math.Sqrt((entity.Position - body).LengthSquared);
            Assert.True(d >= (2d * Radius) - 1e-6, $"the dash overlapped the body at tick {t}: dist={d:F4}");
        }

        Assert.True(entity.Position.X <= body.X + 1e-6, "the dash passed through the body");
        Assert.Equal(2d * Radius, System.Math.Sqrt((entity.Position - body).LengthSquared), 2); // stopped AT contact
    }

    [Fact]
    public void DodgeRoll_OpenGround_CoversTheRollDistance_StaysGrounded()
    {
        var (executor, entity) = Build(spawn: new TileCoord(8, 8));
        var def = DodgeRollDef;
        var origin = entity.Position;

        RunDashToCompletion(executor, entity, def, Direction8.N.ToUnitVector(), serverTick: 100);

        var travelled = System.Math.Sqrt((entity.Position - origin).LengthSquared);
        Assert.Equal(def.ForwardDistanceUnits, travelled, 1e-6); // the full (short) roll distance along the heading
    }

    [Fact]
    public void Determinism_IdenticalChargeTrigger_YieldsByteIdenticalPath_WithAWall()
    {
        // The prediction contract, extended to the dash (mirrors the Phase-A jump determinism test): two independent
        // executors+entities, the same trigger + Step stream, a wall in the path — a BYTE-IDENTICAL XY path the whole
        // way (bit-compared, not epsilon), so the client's replay can reproduce the server's early-stop exactly.
        var blocked = new[] { new TileCoord(11, 8) };
        var (execA, a) = Build(spawn: new TileCoord(8, 8), blocked: blocked);
        var (execB, b) = Build(spawn: new TileCoord(8, 8), blocked: blocked);
        var def = ChargeDef;

        var heading = Direction8.E.ToUnitVector();
        Assert.True(execA.TryStart(a, def, heading, serverTick: 100));
        Assert.True(execB.TryStart(b, def, heading, serverTick: 100));
        AssertBitIdentical(a, b);

        for (var t = 101u; t <= 100u + def.DurationTicks + 2; t++)
        {
            execA.Step(a, t);
            execB.Step(b, t);
            AssertBitIdentical(a, b);
        }
    }

    [Fact]
    public void Determinism_IdenticalDodgeRollTrigger_YieldsByteIdenticalPath()
    {
        var (execA, a) = Build(spawn: new TileCoord(8, 8));
        var (execB, b) = Build(spawn: new TileCoord(8, 8));
        var def = DodgeRollDef;

        var heading = new WorldVector(1d, 1d).Normalized(); // a diagonal roll — exercise a non-cardinal heading
        Assert.True(execA.TryStart(a, def, heading, serverTick: 100));
        Assert.True(execB.TryStart(b, def, heading, serverTick: 100));
        AssertBitIdentical(a, b);

        for (var t = 101u; t <= 100u + def.DurationTicks + 2; t++)
        {
            execA.Step(a, t);
            execB.Step(b, t);
            AssertBitIdentical(a, b);
        }
    }

    [Fact]
    public void Charge_Cooldown_RejectsReTriggerUntilElapsed_ButNotADodgeRoll()
    {
        // Cooldowns are PER (entity, action): a finished charge blocks the NEXT CHARGE until its clock elapses, but
        // never gates a dodge-roll (its own independent clock) — the authoritative model the client's conservative
        // single-slot mirror deliberately under-approximates.
        var (executor, entity) = Build(spawn: new TileCoord(8, 8));
        var charge = ChargeDef;
        var roll = DodgeRollDef;

        Assert.True(executor.TryStart(entity, charge, Direction8.E.ToUnitVector(), serverTick: 100));
        var tick = 100u;
        while (executor.IsActive(entity))
        {
            tick++;
            executor.Step(entity, tick);
        }

        var endTick = tick;
        Assert.False(executor.CanStart(entity, charge, endTick + 1));                      // charge on cooldown
        Assert.True(executor.CanStart(entity, roll, endTick + 1));                         // the roll is not
        Assert.False(executor.CanStart(entity, charge, endTick + charge.CooldownTicks - 1));
        Assert.True(executor.CanStart(entity, charge, endTick + charge.CooldownTicks));    // elapsed — accepted
    }

    [Fact]
    public void IFrames_ActiveExactlyInsideTheDefWindow_AndClearWhenTheActionEnds()
    {
        var (executor, entity) = Build(spawn: new TileCoord(8, 8));
        var def = DodgeRollDef;

        // No action: no i-frames, at any tick.
        Assert.False(executor.HasActiveIFrames(entity.Id, 100));

        Assert.True(executor.TryStart(entity, def, Direction8.E.ToUnitVector(), serverTick: 100));

        // The window is [IFrameStartTick, IFrameEndTick] in elapsed ticks off the start tick — inclusive both ends,
        // false on the trigger tick (start >= 1) and false past the end (the vulnerable recovery tail).
        Assert.False(executor.HasActiveIFrames(entity.Id, 100));
        for (var k = def.IFrameStartTick; k <= def.IFrameEndTick; k++)
        {
            Assert.True(executor.HasActiveIFrames(entity.Id, 100 + k), $"expected i-frames at elapsed {k}");
        }

        Assert.False(executor.HasActiveIFrames(entity.Id, 100 + def.IFrameEndTick + 1));
        // A pre-start tick (uint underflow in the elapsed math) must read false, never wrap into the window.
        Assert.False(executor.HasActiveIFrames(entity.Id, 99));

        // Once the instance ends (Step through the duration), the window is gone even at an "inside" elapsed value —
        // i-frames live exactly as long as the running action.
        var tick = 100u;
        while (executor.IsActive(entity))
        {
            tick++;
            executor.Step(entity, tick);
        }

        Assert.False(executor.HasActiveIFrames(entity.Id, 100 + def.IFrameStartTick));
    }

    [Fact]
    public void IFrames_CancelDropsThemImmediately()
    {
        // A server-cancelled (interrupted) roll loses its i-frames the moment the instance stops — the window can
        // never outlive the action (no lingering invulnerability after an interrupt).
        var (executor, entity) = Build(spawn: new TileCoord(8, 8));
        var def = DodgeRollDef;

        Assert.True(executor.TryStart(entity, def, Direction8.E.ToUnitVector(), serverTick: 100));
        executor.Step(entity, 101);
        Assert.True(executor.HasActiveIFrames(entity.Id, 100 + def.IFrameStartTick));

        executor.Cancel(entity, 101);
        Assert.False(executor.HasActiveIFrames(entity.Id, 100 + def.IFrameStartTick));
    }

    [Fact]
    public void IFrames_NeverOnJumpOrCharge()
    {
        // Only the roll authors a window: a mid-flight jump or charge reports NO i-frames at any elapsed tick.
        var (executor, entity) = Build(spawn: new TileCoord(8, 8));
        var jump = MovementActionRegistry.Default.Get(ActionId.Jump);

        Assert.True(executor.TryStart(entity, jump, Direction8.E.ToUnitVector(), serverTick: 100));
        for (var k = 0u; k <= jump.DurationTicks; k++)
        {
            Assert.False(executor.HasActiveIFrames(entity.Id, 100 + k), $"jump reported i-frames at elapsed {k}");
        }

        var tick = 100u;
        while (executor.IsActive(entity))
        {
            tick++;
            executor.Step(entity, tick);
        }

        tick += jump.CooldownTicks;
        var charge = ChargeDef;
        Assert.True(executor.TryStart(entity, charge, Direction8.E.ToUnitVector(), serverTick: tick));
        for (var k = 0u; k <= charge.DurationTicks; k++)
        {
            Assert.False(executor.HasActiveIFrames(entity.Id, tick + k), $"charge reported i-frames at elapsed {k}");
        }
    }

    private static void AssertBitIdentical(WorldEntity a, WorldEntity b)
    {
        Assert.Equal(System.BitConverter.DoubleToInt64Bits(a.Position.X), System.BitConverter.DoubleToInt64Bits(b.Position.X));
        Assert.Equal(System.BitConverter.DoubleToInt64Bits(a.Position.Y), System.BitConverter.DoubleToInt64Bits(b.Position.Y));
        Assert.Equal(System.BitConverter.DoubleToInt64Bits(a.VerticalOffset), System.BitConverter.DoubleToInt64Bits(b.VerticalOffset));
    }
}
