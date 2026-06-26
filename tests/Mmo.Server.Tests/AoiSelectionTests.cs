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

    // CONTINUOUS MIGRATION (Phase 6): the interest test now measures the continuous float Position, so an
    // entity's in/out-of-AOI flips at its TRUE sub-tile distance — the thing integer-tile distance could not
    // express. Viewer at tile (8,8); unknown candidate; radius 5. Its rounded tile (13,8) is at integer
    // distance exactly 5 (on the boundary, IN under the old integer math), but nudged to x=13.4 its true
    // float distance is 5.4 > 5, so it is now correctly EXCLUDED.
    [Fact]
    public void UnknownEntityJustOutsideFloatRadiusIsExcluded()
    {
        var recipient = new ClientSession(null!);
        var viewer = CreateEntity(networkId: 1, tile: new TileCoord(8, 8));
        var candidate = CreateEntity(networkId: 2, tile: new TileCoord(13, 8));
        candidate.ApplyResolvedMove(new WorldVector(13.4, 8)); // float distance 5.4 > 5

        Assert.False(GameServer.IsEntityInInterest(viewer, candidate, recipient, interestRadius: 5));
    }

    // The mirror inclusion integer math could not express: the candidate's rounded tile (13,8) is at integer
    // distance 5 but its true float position x=12.6 is at distance 4.6 < 5, so it is correctly INCLUDED even
    // for an unknown entity (no hysteresis).
    [Fact]
    public void UnknownEntityJustInsideFloatRadiusIsIncluded()
    {
        var recipient = new ClientSession(null!);
        var viewer = CreateEntity(networkId: 1, tile: new TileCoord(8, 8));
        var candidate = CreateEntity(networkId: 2, tile: new TileCoord(13, 8));
        candidate.ApplyResolvedMove(new WorldVector(12.6, 8)); // float distance 4.6 < 5

        Assert.True(GameServer.IsEntityInInterest(viewer, candidate, recipient, interestRadius: 5));
    }

    // The viewer side is float too: a viewer nudged sub-tile changes who is in interest. Viewer's rounded
    // tile is (8,8) but its true position x=8.4 pulls it toward a candidate at tile (13,8) (true x=13.0):
    // float distance 4.6 < 5 ⇒ included, whereas the rounded-tile distance (5) would have excluded it.
    [Fact]
    public void ViewerSubTilePositionAffectsInterest()
    {
        var recipient = new ClientSession(null!);
        var viewer = CreateEntity(networkId: 1, tile: new TileCoord(8, 8));
        viewer.ApplyResolvedMove(new WorldVector(8.4, 8));
        var candidate = CreateEntity(networkId: 2, tile: new TileCoord(13, 8));

        Assert.True(GameServer.IsEntityInInterest(viewer, candidate, recipient, interestRadius: 5));
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
