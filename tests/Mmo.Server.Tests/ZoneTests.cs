using Mmo.Server.Runtime;
using Mmo.Server.Configuration;
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
        var characterId = Guid.NewGuid();
        session.Authenticate(1, characterId, "Player", ClientRole.Player, Zone.DefaultId);
        var zone = new Zone("test", new TileGrid(8, 8, [new TileCoord(3, 2)]), [new TileCoord(2, 2)]);
        var entity = zone.SpawnPlayer(1, characterId, "Player", new TileCoord(2, 2), session);
        session.AttachEntity(entity);

        Assert.False(zone.TryStep(entity, Direction8.E, serverTick: 10, stepCooldownTicks: 4));
        Assert.Equal(new TileCoord(2, 2), entity.Tile);

        Assert.True(zone.TryStep(entity, Direction8.S, serverTick: 10, stepCooldownTicks: 4));
        Assert.Equal(new TileCoord(2, 3), entity.Tile);
    }

    [Fact]
    public void SpawnPlayerAddsEntityToWorldState()
    {
        var session = new ClientSession(null!);
        var characterId = Guid.NewGuid();
        var zone = new Zone("test", new TileGrid(8, 8, []), [new TileCoord(2, 2)]);

        var entity = zone.SpawnPlayer(4, characterId, "Player", new TileCoord(3, 3), session);

        Assert.True(zone.World.TryGet(entity.Id, out var found));
        Assert.Same(entity, found);
        Assert.Equal(4u, entity.NetworkId);
        Assert.Equal(new TileCoord(3, 3), entity.Tile);
        Assert.True(entity.IsDurable);
    }

    [Fact]
    public void CreateDefaultDistributedSeedsCentralSpawnRegion()
    {
        var zone = Zone.CreateDefault(128, 128, SpawnDistribution.Distributed);

        Assert.True(zone.SpawnTiles.Count > 1);
        Assert.Contains(new TileCoord(64, 64), zone.SpawnTiles);
        Assert.All(zone.SpawnTiles, tile => Assert.True(zone.IsWalkable(tile)));
    }

    [Fact]
    public void CreateDefaultClusteredUsesSingleCentralSpawn()
    {
        var zone = Zone.CreateDefault(128, 128, SpawnDistribution.Clustered);

        var spawn = Assert.Single(zone.SpawnTiles);
        Assert.Equal(new TileCoord(64, 64), spawn);
    }

    [Fact]
    public void CreateDefaultScatteredSpreadsSpawnsAcrossWholeMap()
    {
        const int size = 1000;
        var zone = Zone.CreateDefault(size, size, SpawnDistribution.Scattered);

        Assert.True(zone.SpawnTiles.Count > 1);
        Assert.All(zone.SpawnTiles, tile => Assert.True(zone.IsWalkable(tile)));

        var minX = zone.SpawnTiles.Min(tile => tile.X);
        var maxX = zone.SpawnTiles.Max(tile => tile.X);
        var minY = zone.SpawnTiles.Min(tile => tile.Y);
        var maxY = zone.SpawnTiles.Max(tile => tile.Y);

        // Spawns must reach near both edges of the map, not be confined to the central +/-32 patch
        // that Distributed seeds (which on a 1000-wide map would sit around x in [480, 544]).
        var center = size / 2;
        Assert.True(minX < center - 100, $"minX {minX} should be far left of center {center}");
        Assert.True(maxX > center + 100, $"maxX {maxX} should be far right of center {center}");
        Assert.True(minY < center - 100, $"minY {minY} should be far above center {center}");
        Assert.True(maxY > center + 100, $"maxY {maxY} should be far below center {center}");

        // And they must stay inside the walkable interior (the outer border ring is blocked).
        Assert.True(minX >= 1 && maxX <= size - 2);
        Assert.True(minY >= 1 && maxY <= size - 2);
    }

    [Fact]
    public void CreateDefaultScatteredSkipsBlockedTiles()
    {
        var zone = Zone.CreateDefault(256, 256, SpawnDistribution.Scattered);

        Assert.NotEmpty(zone.BlockedTiles);
        Assert.All(zone.SpawnTiles, tile => Assert.DoesNotContain(tile, zone.BlockedTiles));
    }

    [Fact]
    public void ResolvePlayerSpawnTileDistributesLegacyDefaultTile()
    {
        var zone = Zone.CreateDefault(128, 128, SpawnDistribution.Distributed);

        var first = zone.ResolvePlayerSpawnTile(TileGrid.DefaultSpawnTile);
        var second = zone.ResolvePlayerSpawnTile(TileGrid.DefaultSpawnTile);

        Assert.NotEqual(TileGrid.DefaultSpawnTile, first);
        Assert.NotEqual(first, second);
    }
}
