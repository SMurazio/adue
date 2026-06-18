using Mmo.Server.Runtime;
using Mmo.Shared.Domain;
using Xunit;

namespace Mmo.Server.Tests;

public sealed class ZoneTests
{
    [Fact]
    public void ResolveSpawnTileFallsBackToWalkableZoneSpawn()
    {
        var spawn = new TileCoord(2, 2);
        var zone = new Zone("test", new TileGrid(8, 8, [new TileCoord(1, 1)]), [spawn]);

        Assert.Equal(spawn, zone.ResolveSpawnTile(new TileCoord(1, 1)));
    }

    [Fact]
    public void TryStepValidatesAgainstOwnedTileGrid()
    {
        var session = new ClientSession(null!);
        session.Authenticate(1, Guid.NewGuid(), "Player", ClientRole.Player, Zone.DefaultId, new TileCoord(2, 2));
        var zone = new Zone("test", new TileGrid(8, 8, [new TileCoord(3, 2)]), [new TileCoord(2, 2)]);

        Assert.False(zone.TryStep(session, Direction8.E, serverTick: 10, stepCooldownTicks: 4));
        Assert.Equal(new TileCoord(2, 2), session.Tile);

        Assert.True(zone.TryStep(session, Direction8.S, serverTick: 10, stepCooldownTicks: 4));
        Assert.Equal(new TileCoord(2, 3), session.Tile);
    }
}
