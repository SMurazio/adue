using Mmo.Server.Runtime;
using Mmo.Shared.Domain;
using Xunit;

namespace Mmo.Server.Tests;

public sealed class ResourceNodeTests
{
    private static readonly ResourceNodeDefinition TreeDefinition =
        new("tree", "Tree", YieldItemKey: "wood", YieldQuantity: 2, RespawnTicks: 10);

    [Fact]
    public void NewNodeStartsAvailable()
    {
        var node = new ResourceNode(TreeDefinition);

        Assert.True(node.IsAvailable);
    }

    [Fact]
    public void DepleteMarksUnavailableUntilRespawnTick()
    {
        var node = new ResourceNode(TreeDefinition);

        node.Deplete(serverTick: 100);

        Assert.False(node.IsAvailable);
        Assert.False(node.TryRespawn(serverTick: 109));
        Assert.False(node.IsAvailable);
    }

    [Fact]
    public void RespawnsExactlyAtScheduledTick()
    {
        var node = new ResourceNode(TreeDefinition);
        node.Deplete(serverTick: 100);

        Assert.True(node.TryRespawn(serverTick: 110));
        Assert.True(node.IsAvailable);
    }

    [Fact]
    public void TryRespawnIsNoOpForAvailableNode()
    {
        var node = new ResourceNode(TreeDefinition);

        Assert.False(node.TryRespawn(serverTick: 1000));
        Assert.True(node.IsAvailable);
    }

    [Fact]
    public void RegistryRejectsYieldWithUnknownItem()
    {
        var items = new ItemRegistry([new ItemDefinition("wood", "Wood", 99, ItemCategory.Resource)]);

        Assert.Throws<ArgumentException>(() => new ResourceNodeRegistry(
            [new ResourceNodeDefinition("rock", "Rock", "stone", 1, 50)],
            items));
    }

    [Fact]
    public void DefaultRegistrySeedsTypesMatchingItemRegistry()
    {
        var registry = ResourceNodeRegistry.CreateDefault(ItemRegistry.Default);

        Assert.True(registry.TryGet("tree", out var tree));
        Assert.Equal("wood", tree.YieldItemKey);
        Assert.True(registry.TryGet("rock", out var rock));
        Assert.Equal("stone", rock.YieldItemKey);
        Assert.True(registry.TryGet("plant", out var plant));
        Assert.Equal("fiber", plant.YieldItemKey);
    }

    [Fact]
    public void WorldEntityDepletionBumpsStateRevisionAndDepletedFlag()
    {
        var entity = new WorldEntity(
            id: 1,
            networkId: 1,
            kind: EntityKind.Resource,
            tile: new TileCoord(5, 5),
            facing: Direction8.S,
            displayName: "Tree",
            characterId: null,
            ownerSession: null,
            isDurable: false,
            inventory: null,
            resource: new ResourceNode(TreeDefinition));

        var initialRevision = entity.StateRevision;
        Assert.False(entity.IsDepleted);

        entity.DepleteResource(serverTick: 50);
        Assert.True(entity.IsDepleted);
        Assert.True(entity.StateRevision > initialRevision);

        var depletedRevision = entity.StateRevision;
        Assert.False(entity.TryRespawnResource(serverTick: 59));
        Assert.True(entity.TryRespawnResource(serverTick: 60));
        Assert.False(entity.IsDepleted);
        Assert.True(entity.StateRevision > depletedRevision);
    }
}
