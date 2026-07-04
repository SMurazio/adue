using System.Collections.Generic;
using Mmo.Server.Runtime;
using Mmo.Shared.Domain;
using Xunit;

namespace Mmo.Server.Tests;

// DUO-WAVE2 ability 4 (Midpoint Detonation): the initiate->confirm->charge->blast stepper. Driven against a real
// WorldState with recording seams. Pins: the confirm-timing tier windows (Perfect/Good radius+damage); the LIVE
// midpoint tracking (the marker + the resolve follow the players as they reposition); graceful SOLO degradation (no
// confirm -> a small blast on the initiator); resolve at the FINAL midpoint against monster positions; and the
// lingering slow zone (slows monsters inside for its duration, then expires).
public sealed class MidpointDetonationEngineTests
{
    private static void MoveTo(WorldState world, WorldEntity entity, WorldVector position)
    {
        var previous = entity.TileCoord;
        if (entity.ApplyResolvedMove(position))
        {
            world.OnEntityMoved(entity, previous);
        }
    }

    private sealed class Recorder
    {
        public readonly List<(ulong Monster, int Amount)> Damaged = [];
        public readonly List<ulong> Slowed = [];
        public readonly List<(ulong Target, EchoCueKind Cue)> Cues = [];
        public readonly List<(WorldVector Origin, double Radius, bool Active)> Charges = [];
    }

    private static (MidpointDetonationEngine Engine, Recorder Rec) CreateEngine(WorldState world)
    {
        var rec = new Recorder();
        var engine = new MidpointDetonationEngine(
            world.GatherInterestCandidates,
            (monster, _, amount, _) => rec.Damaged.Add((monster.Id, amount)),
            (monster, _) => rec.Slowed.Add(monster.Id),
            (target, cue) => rec.Cues.Add((target.Id, cue)),
            (_, _, _, origin, radius, _, _, active) => rec.Charges.Add((origin, radius, active)));
        return (engine, rec);
    }

    [Theory]
    [InlineData(3u, MidpointDetonationEngine.PerfectDamage)]  // confirm within Perfect (<= 6 ticks)
    [InlineData(20u, MidpointDetonationEngine.GoodDamage)]    // confirm within Good (<= 30 ticks)
    public void ConfirmTierWindows_BlastAtFinalMidpoint(uint confirmTick, int expectedDamage)
    {
        var world = new WorldState();
        var a = world.AddTransient(1, EntityKind.Player, "A", new TileCoord(10, 10), Direction8.S);
        var b = world.AddTransient(2, EntityKind.Player, "B", new TileCoord(14, 10), Direction8.S);
        var monster = world.AddTransient(10, EntityKind.Monster, "m", new TileCoord(12, 10), Direction8.S);
        var mid = (a.Position + b.Position) * 0.5d;
        MoveTo(world, monster, mid); // sits at the blast centre

        var (engine, rec) = CreateEngine(world);
        engine.PressDetonate(a, b, 0);            // initiate
        Assert.Contains((b.Id, EchoCueKind.DetonateInitiate), rec.Cues);

        engine.PressDetonate(b, a, confirmTick);  // confirm
        Assert.Contains((a.Id, EchoCueKind.DetonateConfirm), rec.Cues);

        // Charge to resolution.
        for (var tick = confirmTick; tick <= confirmTick + MidpointDetonationEngine.ChargeTicks; tick++)
        {
            engine.Step(tick);
        }

        Assert.Contains((monster.Id, expectedDamage), rec.Damaged);
        Assert.Equal(1, engine.SlowZoneCount);                 // the blast left a lingering slow zone
        Assert.Contains(rec.Charges, c => c.Active);           // charge markers streamed while charging
        Assert.Contains(rec.Charges, c => !c.Active);          // and an end-edge on resolve
    }

    [Fact]
    public void LiveMidpointTracking_ResolvesAtTheMovedMidpoint()
    {
        var world = new WorldState();
        var a = world.AddTransient(1, EntityKind.Player, "A", new TileCoord(10, 10), Direction8.S);
        var b = world.AddTransient(2, EntityKind.Player, "B", new TileCoord(14, 10), Direction8.S);
        // A monster at the INITIAL midpoint, and one at the FINAL midpoint after B moves.
        var atInitial = world.AddTransient(10, EntityKind.Monster, "mi", new TileCoord(12, 10), Direction8.S);
        MoveTo(world, atInitial, (a.Position + b.Position) * 0.5d);

        var (engine, rec) = CreateEngine(world);
        engine.PressDetonate(a, b, 0);
        engine.PressDetonate(b, a, 2); // Perfect confirm, charge ends at tick 18

        // Move B far away DURING the charge — the midpoint tracks it, so the blast lands elsewhere.
        MoveTo(world, b, new WorldVector(30d, 10d));
        var finalMid = (a.Position + b.Position) * 0.5d;
        var atFinal = world.AddTransient(11, EntityKind.Monster, "mf", b.TileCoord, Direction8.S);
        MoveTo(world, atFinal, finalMid);

        for (uint tick = 2; tick <= 18; tick++)
        {
            engine.Step(tick);
        }

        // The blast resolved at the FINAL midpoint: the monster there was hit, the one at the initial midpoint was not.
        Assert.Contains(rec.Damaged, d => d.Monster == atFinal.Id);
        Assert.DoesNotContain(rec.Damaged, d => d.Monster == atInitial.Id);
    }

    [Fact]
    public void SoloDegradation_NoConfirm_SmallBlastOnInitiator()
    {
        var world = new WorldState();
        var a = world.AddTransient(1, EntityKind.Player, "A", new TileCoord(10, 10), Direction8.S);
        var monster = world.AddTransient(10, EntityKind.Monster, "m", new TileCoord(10, 10), Direction8.S);
        MoveTo(world, monster, a.Position); // on the initiator

        var (engine, rec) = CreateEngine(world);
        engine.PressDetonate(a, partner: null, serverTick: 0); // solo initiate, never confirmed

        // Step through the confirm window — at ConfirmWindowTicks the solo blast fires on the initiator.
        for (uint tick = 0; tick <= MidpointDetonationEngine.ConfirmWindowTicks; tick++)
        {
            engine.Step(tick);
        }

        Assert.Contains((monster.Id, MidpointDetonationEngine.SoloDamage), rec.Damaged);
        Assert.Equal(0, engine.PendingCount);
    }

    [Fact]
    public void SlowZone_LingersAndSlowsMonstersInside_ThenExpires()
    {
        var world = new WorldState();
        var a = world.AddTransient(1, EntityKind.Player, "A", new TileCoord(10, 10), Direction8.S);
        var b = world.AddTransient(2, EntityKind.Player, "B", new TileCoord(14, 10), Direction8.S);
        var monster = world.AddTransient(10, EntityKind.Monster, "m", new TileCoord(12, 10), Direction8.S);
        MoveTo(world, monster, (a.Position + b.Position) * 0.5d);

        var (engine, rec) = CreateEngine(world);
        engine.PressDetonate(a, b, 0);
        engine.PressDetonate(b, a, 2); // Perfect, resolves at tick 18
        for (uint tick = 2; tick <= 18; tick++)
        {
            engine.Step(tick);
        }

        Assert.Equal(1, engine.SlowZoneCount);
        rec.Slowed.Clear();

        // The zone slows the monster inside it each tick it lingers, then expires after its duration.
        for (uint tick = 19; tick <= 18 + MidpointDetonationEngine.SlowZoneDurationTicks; tick++)
        {
            engine.Step(tick);
        }

        Assert.Contains(monster.Id, rec.Slowed);
        Assert.Equal(0, engine.SlowZoneCount);
    }
}
