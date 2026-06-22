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
        // Due east, 2 tiles out: bearing 0 (on-aim) but distance 2.0 > 1.6 — the radius gate rejects it.
        var dummy = world.AddTransient(2, EntityKind.Dummy, "Dummy", new TileCoord(12, 10), Direction8.S);

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
}
