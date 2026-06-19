using Mmo.Shared.Domain;
using Xunit;

namespace Mmo.Client.Core.Tests;

public sealed class HarvestTargetingTests
{
    [Fact]
    public void PicksAdjacentAvailableResourceNode()
    {
        var entities = new[]
        {
            Resource(10, 6, 5, depleted: false),
        };

        var found = HarvestTargeting.TryFindNearestHarvestable(entities, new TileCoord(5, 5), out var target);

        Assert.True(found);
        Assert.Equal(10u, target);
    }

    [Fact]
    public void IgnoresNodesBeyondOneTile()
    {
        var entities = new[]
        {
            Resource(10, 7, 5, depleted: false),
        };

        var found = HarvestTargeting.TryFindNearestHarvestable(entities, new TileCoord(5, 5), out _);

        Assert.False(found);
    }

    [Fact]
    public void IgnoresDepletedNodes()
    {
        var entities = new[]
        {
            Resource(10, 5, 5, depleted: true),
        };

        var found = HarvestTargeting.TryFindNearestHarvestable(entities, new TileCoord(5, 5), out _);

        Assert.False(found);
    }

    [Fact]
    public void IgnoresNonResourceEntities()
    {
        var entities = new[]
        {
            new EntityRenderState(10, Guid.NewGuid(), EntityKind.Player, "P", default, new TileCoord(5, 5), Direction8.S, false),
        };

        var found = HarvestTargeting.TryFindNearestHarvestable(entities, new TileCoord(5, 5), out _);

        Assert.False(found);
    }

    [Fact]
    public void PrefersNearerNodeThenLowerNetworkIdOnTies()
    {
        var entities = new[]
        {
            Resource(20, 6, 6, depleted: false), // diagonal: euclidean 2
            Resource(10, 6, 5, depleted: false), // orthogonal: euclidean 1 (nearer)
        };

        var found = HarvestTargeting.TryFindNearestHarvestable(entities, new TileCoord(5, 5), out var target);

        Assert.True(found);
        Assert.Equal(10u, target);

        var tie = new[]
        {
            Resource(30, 4, 5, depleted: false), // euclidean 1
            Resource(15, 6, 5, depleted: false), // euclidean 1, lower id wins
        };

        Assert.True(HarvestTargeting.TryFindNearestHarvestable(tie, new TileCoord(5, 5), out var tieTarget));
        Assert.Equal(15u, tieTarget);
    }

    private static EntityRenderState Resource(uint networkId, int x, int y, bool depleted)
    {
        return new EntityRenderState(
            networkId,
            Guid.Empty,
            EntityKind.Resource,
            "Tree",
            default,
            new TileCoord(x, y),
            Direction8.S,
            IsLocal: false,
            Depleted: depleted);
    }
}
