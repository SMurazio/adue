using Mmo.Server.Runtime;
using Mmo.Shared.Domain;
using Xunit;

namespace Mmo.Server.Tests;

// FREEAIM: the geometric free-aim sector resolution against a real WorldState (spatial occupancy + the radius/
// angle test + the friendly-fire gate + damage). The cursor dedup / cooldown gate are tested separately; these
// pin the "who is in the sector and who takes damage" half that GameServer.HandleAttack delegates here.
//
// World mapping: TileCoord (X,Y) -> world (X,0,Y); bearing is atan2(dz, dx) with +X east, +Z south. The attacker
// sits at (10,10). Aim EAST = 0 rad. Defaults mirror the server knobs: 45° half-angle (90° arc), 1.6-tile radius.
public sealed class FreeAimSectorResolverTests
{
    private const int Damage = 20;
    private const double HalfAngle = System.Math.PI / 4d; // 45°
    private const double Radius = 1.6d;
    private const double AimEast = 0d;

    private static WorldEntity Attacker(WorldState world)
        => world.AddPlayer(1, System.Guid.NewGuid(), "Attacker", new TileCoord(10, 10), new ClientSession(null!), new Inventory(ItemRegistry.Default));

    [Fact]
    public void HitsDummyInsideArcAndRadius()
    {
        var world = new WorldState();
        var attacker = Attacker(world);
        // Directly east, 1 tile out: bearing 0 (= aim), distance 1.0 (< 1.6). In-sector.
        var dummy = world.AddTransient(2, EntityKind.Dummy, "Dummy", new TileCoord(11, 10), Direction8.S);

        var hits = FreeAimSectorResolver.ResolveAndDamage(world, attacker, AimEast, HalfAngle, Radius, Damage, []);

        Assert.Equal(1, hits);
        Assert.Equal(80, dummy.Stats.Health);
    }

    [Fact]
    public void MissesDummyOutsideArc()
    {
        var world = new WorldState();
        var attacker = Attacker(world);
        // Due SOUTH, 1 tile out: bearing +π/2 (90°) from the east aim — outside the ±45° arc. Within radius, but the
        // angular gate rejects it.
        var dummy = world.AddTransient(2, EntityKind.Dummy, "Dummy", new TileCoord(10, 11), Direction8.S);

        var hits = FreeAimSectorResolver.ResolveAndDamage(world, attacker, AimEast, HalfAngle, Radius, Damage, []);

        Assert.Equal(0, hits);
        Assert.Equal(100, dummy.Stats.Health);
    }

    [Fact]
    public void MissesDummyBeyondRadius()
    {
        var world = new WorldState();
        var attacker = Attacker(world);
        // Due east, 3 tiles out: bearing 0 (on-aim) but distance 3.0 > radius 1.6 + body 0.5 = 2.1 — even the
        // target's body circle can't reach the sector, so it's a clean miss.
        var dummy = world.AddTransient(2, EntityKind.Dummy, "Dummy", new TileCoord(13, 10), Direction8.S);

        var hits = FreeAimSectorResolver.ResolveAndDamage(world, attacker, AimEast, HalfAngle, Radius, Damage, []);

        Assert.Equal(0, hits);
        Assert.Equal(100, dummy.Stats.Health);
    }

    [Fact]
    public void HitsDiagonalDummyOnTheArcEdge()
    {
        var world = new WorldState();
        var attacker = Attacker(world);
        // NE (11,9): bearing atan2(-1,1) = -45° — exactly at the -halfAngle edge (<= passes); distance √2 ≈ 1.414 < 1.6.
        var dummy = world.AddTransient(2, EntityKind.Dummy, "Dummy", new TileCoord(11, 9), Direction8.S);

        var hits = FreeAimSectorResolver.ResolveAndDamage(world, attacker, AimEast, HalfAngle, Radius, Damage, []);

        Assert.Equal(1, hits);
        Assert.Equal(80, dummy.Stats.Health);
    }

    [Fact]
    public void NoFriendlyFireAgainstOtherPlayers()
    {
        var world = new WorldState();
        var attacker = Attacker(world);
        // A Player squarely in the sector (1 tile east) must be untouched (no friendly fire).
        var ally = world.AddPlayer(2, System.Guid.NewGuid(), "Ally", new TileCoord(11, 10), new ClientSession(null!), new Inventory(ItemRegistry.Default));

        var hits = FreeAimSectorResolver.ResolveAndDamage(world, attacker, AimEast, HalfAngle, Radius, Damage, []);

        Assert.Equal(0, hits);
        Assert.Equal(100, ally.Stats.Health);
    }

    [Fact]
    public void AttackerDoesNotDamageItself()
    {
        var world = new WorldState();
        // A Dummy attacker (an attackable kind) on its own tile must never hit itself, even at point-blank distance 0.
        var attacker = world.AddTransient(1, EntityKind.Dummy, "Self", new TileCoord(10, 10), Direction8.E);

        var hits = FreeAimSectorResolver.ResolveAndDamage(world, attacker, AimEast, HalfAngle, Radius, Damage, []);

        Assert.Equal(0, hits);
        Assert.Equal(100, attacker.Stats.Health);
    }

    [Fact]
    public void HitsEveryEnemyInTheSector()
    {
        var world = new WorldState();
        var attacker = Attacker(world);
        // Three dummies all within the east 90° arc + 1.6 radius: E (11,10), NE (11,9), SE (11,11).
        var d1 = world.AddTransient(2, EntityKind.Dummy, "D1", new TileCoord(11, 10), Direction8.S);
        var d2 = world.AddTransient(3, EntityKind.Dummy, "D2", new TileCoord(11, 9), Direction8.S);
        var d3 = world.AddTransient(4, EntityKind.Dummy, "D3", new TileCoord(11, 11), Direction8.S);

        var hits = FreeAimSectorResolver.ResolveAndDamage(world, attacker, AimEast, HalfAngle, Radius, Damage, []);

        Assert.Equal(3, hits);
        Assert.Equal(80, d1.Stats.Health);
        Assert.Equal(80, d2.Stats.Health);
        Assert.Equal(80, d3.Stats.Health);
    }

    [Fact]
    public void AimRotatesTheSectorContinuously()
    {
        var world = new WorldState();
        var attacker = Attacker(world);
        // The south dummy is OUT of an east-aimed sector but IN a south-aimed one — proving the aim (not a fixed
        // facing) rotates the arc. Aim south = +π/2.
        var dummy = world.AddTransient(2, EntityKind.Dummy, "Dummy", new TileCoord(10, 11), Direction8.S);

        var missEast = FreeAimSectorResolver.ResolveAndDamage(world, attacker, AimEast, HalfAngle, Radius, Damage, []);
        Assert.Equal(0, missEast);
        Assert.Equal(100, dummy.Stats.Health);

        var hitSouth = FreeAimSectorResolver.ResolveAndDamage(world, attacker, System.Math.PI / 2d, HalfAngle, Radius, Damage, []);
        Assert.Equal(1, hitSouth);
        Assert.Equal(80, dummy.Stats.Health);
    }

    [Fact]
    public void DamagedScratchCollectsEachVictimAndAmount()
    {
        // COMBAT-QOL: the overload appends each victim whose HP actually changed (entity + amount) so HandleAttack can
        // emit one cosmetic damage event per real hit. Two dummies in the sector, one Player outside it (no event).
        var world = new WorldState();
        var attacker = Attacker(world);
        var d1 = world.AddTransient(2, EntityKind.Dummy, "D1", new TileCoord(11, 10), Direction8.S);
        var d2 = world.AddTransient(3, EntityKind.Dummy, "D2", new TileCoord(11, 9), Direction8.S);

        var damaged = new System.Collections.Generic.List<FreeAimSectorResolver.DamagedVictim>();
        var hits = FreeAimSectorResolver.ResolveAndDamage(world, attacker, AimEast, HalfAngle, Radius, Damage, [], damaged);

        Assert.Equal(2, hits);
        Assert.Equal(2, damaged.Count);
        Assert.All(damaged, v => Assert.Equal(Damage, v.Amount));
        Assert.Contains(damaged, v => v.Victim.NetworkId == d1.NetworkId);
        Assert.Contains(damaged, v => v.Victim.NetworkId == d2.NetworkId);
    }

    [Fact]
    public void DamagedScratchIsClearedAndEmptyOnAMiss()
    {
        // A resolve that hits nothing must leave the damaged scratch empty (it is cleared up front), so HandleAttack
        // emits no spurious damage events. Pre-fill the list to prove it is cleared.
        var world = new WorldState();
        var attacker = Attacker(world);
        world.AddTransient(2, EntityKind.Dummy, "Dummy", new TileCoord(10, 11), Direction8.S); // due south, outside east arc.

        var damaged = new System.Collections.Generic.List<FreeAimSectorResolver.DamagedVictim>
        {
            new(attacker, 999),
        };
        var hits = FreeAimSectorResolver.ResolveAndDamage(world, attacker, AimEast, HalfAngle, Radius, Damage, [], damaged);

        Assert.Equal(0, hits);
        Assert.Empty(damaged);
    }

    // FREEAIM-PREDICT: the SHARED FreeAimSector.IsHit (which the Godot client uses to PREDICT its own swing) must
    // reproduce the SERVER resolver's hit/miss for every existing scenario. We run the resolver against one dummy at
    // a given tile + aim, observe whether its HP changed (the authoritative hit), and assert the shared geometry
    // helper agrees for the SAME attacker pos (10,10), aim, tuning, body radius, and target pos. If these ever
    // diverge the client would predict a number the server never deals (or miss one it does) — exactly what this pins.
    [Theory]
    [InlineData(11, 10, AimEast, true)]              // due east, in arc + radius.
    [InlineData(10, 11, AimEast, false)]             // due south, outside east arc.
    [InlineData(13, 10, AimEast, false)]             // due east but beyond radius+body.
    [InlineData(11, 9, AimEast, true)]               // NE, on the -45° arc edge.
    [InlineData(11, 11, AimEast, true)]              // SE, on the +45° arc edge.
    [InlineData(10, 11, System.Math.PI / 2d, true)]  // due south, hit by a south-aimed sector.
    [InlineData(10, 11, 0d, false)]                  // due south, missed by the east-aimed sector (aim rotates arc).
    public void SharedHelperReproducesResolverHitMiss(int targetX, int targetY, double aim, bool expectedHit)
    {
        var world = new WorldState();
        var attacker = Attacker(world);
        var dummy = world.AddTransient(2, EntityKind.Dummy, "Dummy", new TileCoord(targetX, targetY), Direction8.S);

        var hits = FreeAimSectorResolver.ResolveAndDamage(world, attacker, aim, HalfAngle, Radius, Damage, []);
        var resolverHit = hits == 1 && dummy.Stats.Health < 100;

        var sharedHit = FreeAimSector.IsHit(
            10d, 10d, aim, HalfAngle, Radius, FreeAimSectorResolver.EntityHitRadiusTiles, targetX, targetY);

        // Both must agree with each other AND with the documented expectation.
        Assert.Equal(expectedHit, resolverHit);
        Assert.Equal(resolverHit, sharedHit);
    }

    [Fact]
    public void SharedHelperPointBlankAlwaysHitsRegardlessOfAim()
    {
        // A target overlapping the attacker (dist <= body radius) is always in-sector, even aimed the opposite way —
        // the resolver's point-blank always-hit, captured by the shared helper. Target half a body radius off-centre,
        // aimed due WEST while the target is to the east.
        var pointBlank = FreeAimSector.IsHit(
            10d, 10d, System.Math.PI, HalfAngle, Radius, FreeAimSectorResolver.EntityHitRadiusTiles, 10.25d, 10d);
        Assert.True(pointBlank);
    }
}
