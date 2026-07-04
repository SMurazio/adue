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
    public void IntegrateMovementValidatesAgainstOwnedTileGrid()
    {
        // CONTINUOUS MIGRATION: the player moves through Zone.IntegrateMovement, which collides the continuous
        // body against walls derived from the Zone's OWNED tile grid. This pins that the Zone wires movement to
        // its own grid: driving EAST toward the blocked (3,2) tile never lets the body enter it, while driving
        // SOUTH into open ground advances the rounded tile. (The exhaustive surface/slide geometry is pinned in
        // ZoneContinuousCollisionTests; this asserts the Zone-spawn-and-own-grid wiring.)
        var session = new ClientSession(null!);
        var characterId = Guid.NewGuid();
        session.Authenticate(1, characterId, "Player", ClientRole.Player, Zone.DefaultId);
        var zone = new Zone("test", new TileGrid(8, 8, [new TileCoord(3, 2)]), [new TileCoord(2, 2)]);
        var entity = zone.SpawnPlayer(1, characterId, "Player", new TileCoord(2, 2), session, new Inventory(ItemRegistry.Default));
        session.AttachEntity(entity);
        entity.SetSpeedUnitsPerSecond(5d);
        const double radius = CollisionDefaults.BodyRadius;

        // Drive EAST into the blocked (3,2): the swept-circle collision stops the body at the wall surface, so it
        // never enters the blocked tile (rounded tile stays at (2,2), never (3,2)).
        for (var i = 0; i < 50; i++)
        {
            zone.IntegrateMovement(entity, Direction8.E.ToUnitVector(), dtSeconds: 0.05d, radius);
        }
        Assert.NotEqual(new TileCoord(3, 2), entity.TileCoord);
        Assert.False(zone.BlockedTiles.Contains(entity.TileCoord));

        // Drive SOUTH into the open (2,3): the body advances unobstructed and its rounded tile crosses to (2,3).
        // Integrate until the rounded tile first reaches y=3 (a bounded loop; 0.25 units/tick reaches y=2.5 in
        // two ticks), then assert it landed on the open tile.
        for (var i = 0; i < 20 && entity.TileCoord.Y < 3; i++)
        {
            zone.IntegrateMovement(entity, Direction8.S.ToUnitVector(), dtSeconds: 0.05d, radius);
        }
        Assert.Equal(new TileCoord(2, 3), entity.TileCoord);
    }

    [Fact]
    public void SpawnPlayerAddsEntityToWorldState()
    {
        var session = new ClientSession(null!);
        var characterId = Guid.NewGuid();
        var zone = new Zone("test", new TileGrid(8, 8, []), [new TileCoord(2, 2)]);

        var entity = zone.SpawnPlayer(4, characterId, "Player", new TileCoord(3, 3), session, new Inventory(ItemRegistry.Default));

        Assert.True(zone.World.TryGet(entity.Id, out var found));
        Assert.Same(entity, found);
        Assert.Equal(4u, entity.NetworkId);
        Assert.Equal(new TileCoord(3, 3), entity.TileCoord);
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

    // NODE-FIELD N2: the six PlanResourceNodeScatter/HasClearApproachRoom tests that lived here (determinism,
    // min-spacing, distribution, approach-room) were REMOVED along with Zone.PlanResourceNodeScatter itself —
    // scattered harvestables are catalogue entries now, built by the shared NodeCatalog (Mmo.Shared) instead of
    // Zone. The equivalent coverage lives in NodeCatalogTests (Mmo.Shared.Tests): determinism/distribution/
    // pin-stability/grass-and-off-marker invariants, plus the D8 total-count floor. See docs/node-field-design.md.

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
