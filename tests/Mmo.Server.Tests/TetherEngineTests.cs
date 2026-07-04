using System.Collections.Generic;
using Mmo.Server.Runtime;
using Mmo.Shared.Domain;
using Xunit;

namespace Mmo.Server.Tests;

// DUO-WAVE2 ability 3 (Laser Tether): the beam stepper + its pure band/damage math. Driven against a real WorldState
// (the SAME spatial gather GameServer wires) with recording fakes for the monster-damage / monster-slow / player-
// damage seams. Pins: the band boundaries + the toward-the-middle damage scaling (TetherMath); the ORBIT-SWEEP case
// (a stationary partner, an orbiting player, monsters on the chord each take ticks as the beam sweeps them); and the
// overstretch DoT to BOTH players + the break-after-2s + the 3s re-link cooldown.
public sealed class TetherEngineTests
{
    private static void MoveTo(WorldState world, WorldEntity entity, WorldVector position)
    {
        var previous = entity.TileCoord;
        if (entity.ApplyResolvedMove(position))
        {
            world.OnEntityMoved(entity, previous);
        }
    }

    [Fact]
    public void Band_AtBoundaries()
    {
        Assert.Equal(TetherBand.Inert, TetherMath.Band(2.99d, 3d, 10d, 12d));
        Assert.Equal(TetherBand.Sweet, TetherMath.Band(3d, 3d, 10d, 12d));    // inclusive lower edge
        Assert.Equal(TetherBand.Sweet, TetherMath.Band(10d, 3d, 10d, 12d));   // inclusive upper edge
        Assert.Equal(TetherBand.Warning, TetherMath.Band(10.5d, 3d, 10d, 12d));
        Assert.Equal(TetherBand.Warning, TetherMath.Band(11.99d, 3d, 10d, 12d));
        Assert.Equal(TetherBand.Overstretch, TetherMath.Band(12d, 3d, 10d, 12d)); // inclusive overstretch edge
        Assert.Equal(TetherBand.Overstretch, TetherMath.Band(20d, 3d, 10d, 12d));
    }

    [Fact]
    public void SweetTickDamage_PeaksAtTheMiddle_MinAtEdges()
    {
        // Sweet band [3,10], mid 6.5, damage [2,5].
        Assert.Equal(5, TetherMath.SweetTickDamage(6.5d, 3d, 10d, 6.5d, 2, 5));  // dead centre
        Assert.Equal(2, TetherMath.SweetTickDamage(3d, 3d, 10d, 6.5d, 2, 5));    // low edge
        Assert.Equal(2, TetherMath.SweetTickDamage(10d, 3d, 10d, 6.5d, 2, 5));   // high edge
        var mid = TetherMath.SweetTickDamage(4.75d, 3d, 10d, 6.5d, 2, 5);        // halfway to the edge
        Assert.InRange(mid, 3, 4);
    }

    private sealed class Recorder
    {
        public readonly List<ulong> DamagedMonsters = [];
        public readonly List<ulong> SlowedMonsters = [];
        public readonly List<(ulong Victim, int Amount)> PlayerDamage = [];
        public readonly List<TetherState> Statuses = [];
    }

    private static (TetherEngine Engine, Recorder Rec) CreateEngine(WorldState world)
    {
        var rec = new Recorder();
        var engine = new TetherEngine(
            world.GatherInterestCandidates,
            (monster, _, amount, _) => rec.DamagedMonsters.Add(monster.Id),
            (monster, _) => rec.SlowedMonsters.Add(monster.Id),
            (victim, amount, _, _) => { rec.PlayerDamage.Add((victim.Id, amount)); return true; },
            (_, _, state) => rec.Statuses.Add(state));
        return (engine, rec);
    }

    [Fact]
    public void OrbitSweep_MonstersOnTheChord_EachTakeTicks_AsTheBeamSweeps()
    {
        var world = new WorldState();
        // Partner stationary at (10,10); the moving player orbits at the sweet-spot radius 6.5.
        var partner = world.AddTransient(1, EntityKind.Player, "B", new TileCoord(10, 10), Direction8.S);
        var mover = world.AddTransient(2, EntityKind.Player, "A", new TileCoord(16, 10), Direction8.S);
        MoveTo(world, partner, new WorldVector(10d, 10d));

        // Monsters at distance 3 from the partner along east / south / west — each lies ON the beam segment when the
        // mover is 6.5 out in that same direction.
        var mEast = world.AddTransient(10, EntityKind.Monster, "mE", new TileCoord(13, 10), Direction8.S);
        var mSouth = world.AddTransient(11, EntityKind.Monster, "mS", new TileCoord(10, 13), Direction8.S);
        var mWest = world.AddTransient(12, EntityKind.Monster, "mW", new TileCoord(7, 10), Direction8.S);
        MoveTo(world, mEast, new WorldVector(13d, 10d));
        MoveTo(world, mSouth, new WorldVector(10d, 13d));
        MoveTo(world, mWest, new WorldVector(7d, 10d));

        var (engine, rec) = CreateEngine(world);
        engine.Toggle(mover, partner, 0); // NextSweetDamageTick seeded to tick 0

        // tick 0: mover 6.5 east — beam sweeps mEast (a damage tick, 0 >= 0).
        MoveTo(world, mover, new WorldVector(16.5d, 10d));
        engine.Step(0, 1d / 20d);

        // tick 5: mover 6.5 south — the next damage tick sweeps mSouth.
        MoveTo(world, mover, new WorldVector(10d, 16.5d));
        engine.Step(5, 1d / 20d);

        // tick 10: mover 6.5 west — sweeps mWest.
        MoveTo(world, mover, new WorldVector(3.5d, 10d));
        engine.Step(10, 1d / 20d);

        Assert.Contains(mEast.Id, rec.DamagedMonsters);
        Assert.Contains(mSouth.Id, rec.DamagedMonsters);
        Assert.Contains(mWest.Id, rec.DamagedMonsters);
        // Each swept monster was also briefly slowed.
        Assert.Contains(mEast.Id, rec.SlowedMonsters);
        Assert.Contains(mSouth.Id, rec.SlowedMonsters);
        Assert.Contains(mWest.Id, rec.SlowedMonsters);
    }

    [Fact]
    public void Overstretch_DamagesBothPlayers_ThenBreaks_ThenRelinkCooldownGatesToggle()
    {
        var world = new WorldState();
        var a = world.AddTransient(1, EntityKind.Player, "A", new TileCoord(10, 10), Direction8.S);
        var b = world.AddTransient(2, EntityKind.Player, "B", new TileCoord(10, 25), Direction8.S);
        MoveTo(world, a, new WorldVector(10d, 10d));
        MoveTo(world, b, new WorldVector(10d, 25d)); // 15 units apart — overstretch (>= 12)

        var (engine, rec) = CreateEngine(world);
        Assert.Equal(TetherState.On, engine.Toggle(a, b, 0));

        // Step through the 2s (40-tick) overstretch break window.
        for (uint tick = 0; tick <= TetherEngine.OverstretchBreakTicks; tick++)
        {
            engine.Step(tick, 1d / 20d);
        }

        // Broke: no active tether, a Broken status fired, and BOTH players took the DoT.
        Assert.Equal(0, engine.ActiveCount);
        Assert.Contains(TetherState.Broken, rec.Statuses);
        Assert.Contains(rec.PlayerDamage, d => d.Victim == a.Id);
        Assert.Contains(rec.PlayerDamage, d => d.Victim == b.Id);

        // Re-link cooldown (3s = 60 ticks) gates a fresh toggle: rejected mid-cooldown, allowed after.
        Assert.Equal(TetherState.Off, engine.Toggle(a, b, TetherEngine.OverstretchBreakTicks + 5));
        Assert.Equal(0, engine.ActiveCount);
        Assert.Equal(TetherState.On, engine.Toggle(a, b, TetherEngine.OverstretchBreakTicks + TetherEngine.RelinkCooldownTicks + 1));
        Assert.Equal(1, engine.ActiveCount);
    }
}
