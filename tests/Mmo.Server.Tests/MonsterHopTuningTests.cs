using System.Collections.Generic;
using Mmo.Server.Runtime;
using Mmo.Shared.Domain;
using Mmo.Shared.Domain.Actions;
using Xunit;

namespace Mmo.Server.Tests;

// DATA-DRIVEN monster tuning (v40): the slime hop is now tuned by the per-type hopDistance / hopHeight / hopAirborneMs
// knobs, and the hop arc is DECOUPLED from the move cadence — BeginMonsterHop builds the ballistic Jump's DurationTicks
// from HopAirborneTicks (a SHORT airborne span), NOT the whole move cadence, so the slime RESTS on the ground between
// hops (the "hops too often" fix). These tests drive the SAME shared ServerActionExecutor + MonsterTypeRegistry the
// server uses, mirroring GameServer.BeginMonsterHop, and pin that (a) the executor-driven hop covers HopDistanceUnits,
// (b) tuning hopDistance changes the distance covered, and (c) tuning hopAirborneMs sets the airborne span, leaving a
// grounded rest before the cadence would re-arm.
public sealed class MonsterHopTuningTests
{
    private const int TickRate = 20;
    private const double Radius = CollisionDefaults.BodyRadius; // 0.5

    // Builds the executor over an OPEN TileGrid (the real shared collision derivation) + a fresh Monster entity.
    private static (ServerActionExecutor executor, WorldEntity monster) Build()
    {
        var grid = new TileGrid(64, 64, System.Array.Empty<TileCoord>());
        var executor = new ServerActionExecutor(
            TickRate,
            () => Radius,
            grid.QueryNearbyWalls,
            (entity, resolved) => entity.ApplyResolvedMove(resolved));

        var monster = new WorldEntity(
            id: 1, networkId: 1, EntityKind.Monster,
            new TileCoord(16, 16), Direction8.S, "Slime",
            characterId: null, ownerSession: null, isDurable: false);
        monster.SetSpeedUnitsPerSecond(5d);
        return (executor, monster);
    }

    // Mirrors GameServer.BeginMonsterHop EXACTLY: DurationTicks = registry.HopAirborneTicks(type), forward distance =
    // type.HopDistanceUnits, height = type.HopHeightUnits, cooldown 0. Returns the executor TryStart result.
    private static bool BeginHop(
        ServerActionExecutor executor, WorldEntity monster, MonsterTypeRegistry registry, MonsterType type,
        WorldVector heading, uint serverTick)
    {
        var def = MovementActionRegistry.BuildForwardArcJump(
            ActionId.Jump,
            durationTicks: registry.HopAirborneTicks(type),
            jumpHeight: type.HopHeightUnits,
            forwardDistanceUnits: type.HopDistanceUnits,
            cooldownTicks: 0,
            animationId: 1);
        return executor.TryStart(monster, def, heading, serverTick);
    }

    // Steps the executor `ticks` times from serverTick+1 and returns the entity's IsActive AFTER the last step.
    private static void Step(ServerActionExecutor executor, WorldEntity monster, uint fromTick, uint ticks)
    {
        for (uint i = 1; i <= ticks; i++)
        {
            executor.Step(monster, fromTick + i);
        }
    }

    [Fact]
    public void DefaultHopCoversHopDistanceThenRestsGroundedBeforeTheCadence()
    {
        var registry = new MonsterTypeRegistry(TickRate);
        var slime = registry.Default;
        var (executor, monster) = Build();
        var origin = monster.Position;

        var airborne = registry.HopAirborneTicks(slime);
        Assert.Equal(6u, airborne); // 300 ms @ 20 Hz.

        Assert.True(BeginHop(executor, monster, registry, slime, Direction8.E.ToUnitVector(), serverTick: 0));
        Step(executor, monster, fromTick: 0, ticks: airborne);

        // Landed: XY advanced exactly the (bumped) default HopDistanceUnits east, Z snapped to ground, action ended.
        Assert.Equal(origin.X + 1.5d, monster.Position.X, 1e-6);
        Assert.Equal(origin.Y, monster.Position.Y, 1e-6);
        Assert.Equal(0d, monster.VerticalOffset, 1e-9);
        Assert.False(executor.IsActive(monster));

        // The "hops too often" fix: the move cadence (8 ticks at the 0.6x default) is LONGER than the airborne span, so
        // after landing there is a GROUNDED REST tick where no action is active and the monster is on the ground.
        const uint cadence = 8u;
        Assert.True(airborne < cadence);
        Step(executor, monster, fromTick: airborne, ticks: 1); // a tick within the cadence window, after landing.
        Assert.False(executor.IsActive(monster));               // resting (no arc in flight).
        Assert.Equal(0d, monster.VerticalOffset, 1e-9);          // and grounded.
    }

    [Fact]
    public void TuningHopDistanceChangesTheDistanceCovered()
    {
        var registry = new MonsterTypeRegistry(TickRate);
        var slime = registry.Default;

        // Default reach.
        {
            var (executor, monster) = Build();
            var origin = monster.Position;
            Assert.True(BeginHop(executor, monster, registry, slime, Direction8.E.ToUnitVector(), serverTick: 0));
            Step(executor, monster, fromTick: 0, ticks: registry.HopAirborneTicks(slime));
            Assert.Equal(origin.X + 1.5d, monster.Position.X, 1e-6);
        }

        // Tune the reach UP and confirm a longer hop.
        Assert.True(registry.TryApply("slime.hopDistance", 4.0d, out var applied));
        Assert.Equal(4.0d, applied, 6);
        {
            var (executor, monster) = Build();
            var origin = monster.Position;
            Assert.True(BeginHop(executor, monster, registry, slime, Direction8.E.ToUnitVector(), serverTick: 0));
            Step(executor, monster, fromTick: 0, ticks: registry.HopAirborneTicks(slime));
            Assert.Equal(origin.X + 4.0d, monster.Position.X, 1e-6);
        }
    }

    [Fact]
    public void TuningHopAirborneMsSetsTheAirborneSpan()
    {
        var registry = new MonsterTypeRegistry(TickRate);
        var slime = registry.Default;

        // Lengthen the airborne span to 1000 ms = 20 ticks; the executor stays active for exactly that many ticks.
        Assert.True(registry.TryApply("slime.hopAirborneMs", 1000d, out _));
        Assert.Equal(20u, registry.HopAirborneTicks(slime));

        var (executor, monster) = Build();
        Assert.True(BeginHop(executor, monster, registry, slime, Direction8.E.ToUnitVector(), serverTick: 0));

        // Active through tick 19, lands (inactive) at tick 20.
        Step(executor, monster, fromTick: 0, ticks: 19);
        Assert.True(executor.IsActive(monster));
        Assert.True(monster.VerticalOffset > 0d); // still airborne mid-arc.

        Step(executor, monster, fromTick: 19, ticks: 1);
        Assert.False(executor.IsActive(monster));
        Assert.Equal(0d, monster.VerticalOffset, 1e-9);
    }
}
