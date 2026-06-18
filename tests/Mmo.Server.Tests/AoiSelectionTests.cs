using Mmo.Server.Runtime;
using Mmo.Shared.Domain;
using Xunit;

namespace Mmo.Server.Tests;

public sealed class AoiSelectionTests
{
    [Fact]
    public void KnownEntityStaysInInterestThroughExitHysteresis()
    {
        var recipient = new ClientSession(null!);
        var viewer = CreateEntity(networkId: 1, tile: new TileCoord(8, 8));
        var candidate = CreateEntity(networkId: 2, tile: new TileCoord(14, 8));
        recipient.RememberSnapshotEntities([candidate]);

        Assert.True(GameServer.IsEntityInInterest(viewer, candidate, recipient, interestRadius: 5));
    }

    [Fact]
    public void UnknownEntityMustEnterConfiguredRadius()
    {
        var recipient = new ClientSession(null!);
        var viewer = CreateEntity(networkId: 1, tile: new TileCoord(8, 8));
        var candidate = CreateEntity(networkId: 2, tile: new TileCoord(14, 8));

        Assert.False(GameServer.IsEntityInInterest(viewer, candidate, recipient, interestRadius: 5));
    }

    [Fact]
    public void KnownEntityLeavesInterestPastExitHysteresis()
    {
        var recipient = new ClientSession(null!);
        var viewer = CreateEntity(networkId: 1, tile: new TileCoord(8, 8));
        var candidate = CreateEntity(networkId: 2, tile: new TileCoord(15, 8));
        recipient.RememberSnapshotEntities([candidate]);

        Assert.False(GameServer.IsEntityInInterest(viewer, candidate, recipient, interestRadius: 5));
    }

    private static WorldEntity CreateEntity(uint networkId, TileCoord tile)
    {
        return new WorldEntity(
            id: networkId,
            networkId: networkId,
            EntityKind.Player,
            tile,
            Direction8.S,
            $"Entity{networkId}",
            Guid.NewGuid(),
            ownerSession: null,
            isDurable: true);
    }
}
