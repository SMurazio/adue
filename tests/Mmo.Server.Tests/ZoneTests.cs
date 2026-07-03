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

    [Fact]
    public void PlanResourceNodeScatterSpreadsAcrossWholeWalkableMap()
    {
        const int size = 1000;
        var zone = Zone.CreateDefault(size, size, SpawnDistribution.Clustered);
        var registry = ResourceNodeRegistry.CreateDefault(ItemRegistry.Default);

        var placements = zone.PlanResourceNodeScatter(registry, densityTilesPerNode: 28);

        Assert.NotEmpty(placements);
        // Every placed tile is walkable and unique.
        Assert.All(placements, p => Assert.True(zone.IsWalkable(p.Tile)));
        Assert.Equal(placements.Count, placements.Select(p => p.Tile).Distinct().Count());

        // Count is in the neighbourhood of the density target (1 per 28² tiles ≈ 1276 on a 1000² map).
        var target = (size * size) / (28 * 28);
        Assert.InRange(placements.Count, target * 8 / 10, target);

        // Placement reaches well into all four quadrants — not confined near a single cluster.
        var center = size / 2;
        Assert.True(placements.Min(p => p.Tile.X) < center - 200);
        Assert.True(placements.Max(p => p.Tile.X) > center + 200);
        Assert.True(placements.Min(p => p.Tile.Y) < center - 200);
        Assert.True(placements.Max(p => p.Tile.Y) > center + 200);

        // A rough even type mix: every registered type appears.
        var types = placements.Select(p => p.Definition.Key).Distinct().ToHashSet();
        Assert.Contains("tree", types);
        Assert.Contains("rock", types);
        Assert.Contains("plant", types);
    }

    [Fact]
    public void PlanResourceNodeScatterIsDeterministicForSameSeed()
    {
        var registry = ResourceNodeRegistry.CreateDefault(ItemRegistry.Default);
        var a = Zone.CreateGenerated(256, 256, seed: 1234, genVersion: 1);
        var b = Zone.CreateGenerated(256, 256, seed: 1234, genVersion: 1);

        var first = a.PlanResourceNodeScatter(registry, densityTilesPerNode: 24);
        var second = b.PlanResourceNodeScatter(registry, densityTilesPerNode: 24);

        Assert.Equal(first.Count, second.Count);
        for (var i = 0; i < first.Count; i++)
        {
            Assert.Equal(first[i].Tile, second[i].Tile);
            Assert.Equal(first[i].Definition.Key, second[i].Definition.Key);
        }
    }

    [Fact]
    public void PlanResourceNodeScatterDiffersForDifferentSeed()
    {
        var registry = ResourceNodeRegistry.CreateDefault(ItemRegistry.Default);
        var a = Zone.CreateGenerated(256, 256, seed: 1, genVersion: 1);
        var b = Zone.CreateGenerated(256, 256, seed: 2, genVersion: 1);

        var first = a.PlanResourceNodeScatter(registry, densityTilesPerNode: 24);
        var second = b.PlanResourceNodeScatter(registry, densityTilesPerNode: 24);

        // Different map seeds should yield a different layout (not byte-identical tile sequences).
        var firstTiles = first.Select(p => p.Tile).ToList();
        var secondTiles = second.Select(p => p.Tile).ToList();
        Assert.NotEqual(firstTiles, secondTiles);
    }

    [Fact]
    public void PlanResourceNodeScatterRespectsMinSpacing()
    {
        var zone = Zone.CreateGenerated(256, 256, seed: 7, genVersion: 1);
        var registry = ResourceNodeRegistry.CreateDefault(ItemRegistry.Default);

        const int density = 24;
        var placements = zone.PlanResourceNodeScatter(registry, densityTilesPerNode: density);
        var minSpacing = Math.Max(1, (density * 7) / 10);

        // No two placed nodes are closer than the min spacing (Chebyshev), so they don't clump.
        for (var i = 0; i < placements.Count; i++)
        {
            for (var j = i + 1; j < placements.Count; j++)
            {
                var dx = Math.Abs(placements[i].Tile.X - placements[j].Tile.X);
                var dy = Math.Abs(placements[i].Tile.Y - placements[j].Tile.Y);
                Assert.True(Math.Max(dx, dy) >= minSpacing);
            }
        }
    }

    [Fact]
    public void PlanResourceNodeScatterDisabledByZeroDensity()
    {
        var zone = Zone.CreateGenerated(128, 128, seed: 0, genVersion: 1);
        var registry = ResourceNodeRegistry.CreateDefault(ItemRegistry.Default);

        Assert.Empty(zone.PlanResourceNodeScatter(registry, densityTilesPerNode: 0));
    }

    [Fact]
    public void PlanResourceNodeScatterGivesEveryNodeClearApproachRoom()
    {
        // S48: a node must never sit jammed against a wall/border (the play-test bug placed a Tree at
        // (1,47), 3 of whose 8 neighbours were the X=0 border). Build a map with the perimeter border ring
        // plus an interior wall segment, then assert every placed node has all 8 Chebyshev neighbours
        // walkable — so no node is adjacent to a blocked or out-of-bounds tile.
        const int size = 64;
        var blocked = new List<TileCoord>();
        for (var i = 0; i < size; i++)
        {
            blocked.Add(new TileCoord(i, 0));
            blocked.Add(new TileCoord(i, size - 1));
            blocked.Add(new TileCoord(0, i));
            blocked.Add(new TileCoord(size - 1, i));
        }

        // An interior wall segment so neighbour-rejection is exercised away from the border too.
        for (var y = 20; y <= 40; y++)
        {
            blocked.Add(new TileCoord(32, y));
        }

        var zone = new Zone("test", new TileGrid(size, size, blocked), [new TileCoord(10, 10)]);
        var registry = ResourceNodeRegistry.CreateDefault(ItemRegistry.Default);

        var placements = zone.PlanResourceNodeScatter(registry, densityTilesPerNode: 6);

        Assert.NotEmpty(placements);
        Assert.All(placements, p =>
        {
            for (var dy = -1; dy <= 1; dy++)
            {
                for (var dx = -1; dx <= 1; dx++)
                {
                    var neighbour = new TileCoord(p.Tile.X + dx, p.Tile.Y + dy);
                    Assert.True(
                        zone.IsWalkable(neighbour),
                        $"Node at {p.Tile} has non-walkable neighbour {neighbour}");
                }
            }
        });
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
