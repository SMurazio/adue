using Mmo.Server.Runtime;
using Mmo.Shared.Domain;
using Xunit;

namespace Mmo.Server.Tests;

// COMBAT-S2B: the end-to-end melee-cone resolution against a real WorldState (spatial occupancy + friendly-fire
// gate + damage). The cursor dedup / cooldown gate are tested separately (ClientSessionTests / WorldEntityCombat);
// these pin the "who is on the cone and who takes damage" half that GameServer.HandleAttack delegates here.
public sealed class MeleeConeResolverTests
{
    private const int Damage = 20;

    [Fact]
    public void DamagesDummyOnTheForwardConeTile()
    {
        var world = new WorldState();
        // Attacker at (10,10) facing E → cone forward tile is (11,10).
        // Spawn one tile WEST and step E so the attacker ends at (10,10) FACING E (TryStep sets facing on the step).
        var attacker = world.AddPlayer(1, Guid.NewGuid(), "Attacker", new TileCoord(9, 10), new ClientSession(null!), new Inventory(ItemRegistry.Default));
        FaceEast(attacker, world);
        Assert.Equal(new TileCoord(10, 10), attacker.TileCoord);
        Assert.Equal(Direction8.E, attacker.Facing);
        var dummy = world.AddTransient(2, EntityKind.Dummy, "Dummy", new TileCoord(11, 10), Direction8.S);

        var hits = MeleeConeResolver.ResolveAndDamage(world, attacker, Damage, []);

        Assert.Equal(1, hits);
        Assert.Equal(80, dummy.Stats.Health);
    }

    [Fact]
    public void DamagesDummyOnAFlankConeTile()
    {
        var world = new WorldState();
        // Spawn one tile WEST and step E so the attacker ends at (10,10) FACING E (TryStep sets facing on the step).
        var attacker = world.AddPlayer(1, Guid.NewGuid(), "Attacker", new TileCoord(9, 10), new ClientSession(null!), new Inventory(ItemRegistry.Default));
        FaceEast(attacker, world);
        Assert.Equal(new TileCoord(10, 10), attacker.TileCoord);
        Assert.Equal(Direction8.E, attacker.Facing);
        // Facing E → flanks are NE (11,9) and SE (11,11). Put a dummy on the NE flank.
        var dummy = world.AddTransient(2, EntityKind.Dummy, "Dummy", new TileCoord(11, 9), Direction8.S);

        var hits = MeleeConeResolver.ResolveAndDamage(world, attacker, Damage, []);

        Assert.Equal(1, hits);
        Assert.Equal(80, dummy.Stats.Health);
    }

    [Fact]
    public void DoesNotDamageDummyOffTheCone()
    {
        var world = new WorldState();
        // Spawn one tile WEST and step E so the attacker ends at (10,10) FACING E (TryStep sets facing on the step).
        var attacker = world.AddPlayer(1, Guid.NewGuid(), "Attacker", new TileCoord(9, 10), new ClientSession(null!), new Inventory(ItemRegistry.Default));
        FaceEast(attacker, world);
        Assert.Equal(new TileCoord(10, 10), attacker.TileCoord);
        Assert.Equal(Direction8.E, attacker.Facing);
        // Behind the attacker (W) is NOT in the E-facing cone.
        var dummy = world.AddTransient(2, EntityKind.Dummy, "Dummy", new TileCoord(9, 10), Direction8.S);

        var hits = MeleeConeResolver.ResolveAndDamage(world, attacker, Damage, []);

        Assert.Equal(0, hits);
        Assert.Equal(100, dummy.Stats.Health);
    }

    [Fact]
    public void NoFriendlyFireAgainstOtherPlayers()
    {
        var world = new WorldState();
        // Spawn one tile WEST and step E so the attacker ends at (10,10) FACING E (TryStep sets facing on the step).
        var attacker = world.AddPlayer(1, Guid.NewGuid(), "Attacker", new TileCoord(9, 10), new ClientSession(null!), new Inventory(ItemRegistry.Default));
        FaceEast(attacker, world);
        Assert.Equal(new TileCoord(10, 10), attacker.TileCoord);
        Assert.Equal(Direction8.E, attacker.Facing);
        // Another PLAYER standing on the forward cone tile must be untouched.
        var ally = world.AddPlayer(2, Guid.NewGuid(), "Ally", new TileCoord(11, 10), new ClientSession(null!), new Inventory(ItemRegistry.Default));

        var hits = MeleeConeResolver.ResolveAndDamage(world, attacker, Damage, []);

        Assert.Equal(0, hits);
        Assert.Equal(100, ally.Stats.Health);
    }

    [Fact]
    public void DamagesEveryEnemyOnTheCone()
    {
        var world = new WorldState();
        // Spawn one tile WEST and step E so the attacker ends at (10,10) FACING E (TryStep sets facing on the step).
        var attacker = world.AddPlayer(1, Guid.NewGuid(), "Attacker", new TileCoord(9, 10), new ClientSession(null!), new Inventory(ItemRegistry.Default));
        FaceEast(attacker, world);
        Assert.Equal(new TileCoord(10, 10), attacker.TileCoord);
        Assert.Equal(Direction8.E, attacker.Facing);
        // Facing E → cone tiles E (11,10), NE (11,9), SE (11,11). A dummy on each.
        var d1 = world.AddTransient(2, EntityKind.Dummy, "D1", new TileCoord(11, 10), Direction8.S);
        var d2 = world.AddTransient(3, EntityKind.Dummy, "D2", new TileCoord(11, 9), Direction8.S);
        var d3 = world.AddTransient(4, EntityKind.Dummy, "D3", new TileCoord(11, 11), Direction8.S);

        var hits = MeleeConeResolver.ResolveAndDamage(world, attacker, Damage, []);

        Assert.Equal(3, hits);
        Assert.Equal(80, d1.Stats.Health);
        Assert.Equal(80, d2.Stats.Health);
        Assert.Equal(80, d3.Stats.Health);
    }

    [Fact]
    public void AttackerDoesNotDamageItself()
    {
        var world = new WorldState();
        // A Dummy as the attacker (it IS an attackable kind) — it must still never hit itself even if its own tile
        // somehow appeared in the candidate set.
        var attacker = world.AddTransient(1, EntityKind.Dummy, "Self", new TileCoord(10, 10), Direction8.E);

        var hits = MeleeConeResolver.ResolveAndDamage(world, attacker, Damage, []);

        Assert.Equal(0, hits);
        Assert.Equal(100, attacker.Stats.Health);
    }

    // Players spawn facing S (Direction8.S). Step E once so the entity faces E (TryStep sets facing on the step),
    // putting the cone to the east where the tests place dummies. The grid is large + empty so the step succeeds.
    private static void FaceEast(WorldEntity entity, WorldState world)
    {
        var grid = new TileGrid(64, 64, []);
        var previous = entity.TileCoord;
        Assert.True(entity.TryStep(Direction8.E, serverTick: 0, stepCooldownTicks: 0, grid));
        world.OnEntityMoved(entity, previous);
    }
}
