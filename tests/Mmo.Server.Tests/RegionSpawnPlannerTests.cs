using Mmo.Server.Runtime;
using Mmo.Shared.Domain;
using Xunit;

namespace Mmo.Server.Tests;

// ECOLOGY E2 (docs/ecology-v1-design.md §5.2; docs/procedural-population-design.md D5): headless coverage for
// RegionSpawnPlanner — the PURE, static derivation of a region×type's spawnTiles. No Zone/GameServer/TileGrid
// dependency (mirrors EcologyStateTests' "headlessly testable" philosophy): candidates come from a plain
// isWalkable lambda + an optional synthetic AuthoredMap, so these tests pin the math itself, independent of any
// live server wiring.
public sealed class RegionSpawnPlannerTests
{
    private static EcologyRegion Region(string id, int minX, int minY, int maxX, int maxY) =>
        new(id, id, minX, minY, maxX, maxY, new Dictionary<string, EcologyTypeConfig>(StringComparer.OrdinalIgnoreCase)
        {
            ["t"] = new EcologyTypeConfig(10d, 1.0d, 10),
        });

    private static double ChebyshevDistance(TileCoord a, TileCoord b) =>
        Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    // §5.2 "derived spawn tiles are deterministic": identical inputs -> byte-identical output, every time.
    [Fact]
    public void DeriveSpawnTiles_SameInputs_ProducesIdenticalResults()
    {
        var region = Region("r", 1, 1, 38, 38);
        var field = RegionSpawnPlanner.ComputeRoadDistanceField(authoredMap: null, width: 40, height: 40);

        var first = RegionSpawnPlanner.DeriveSpawnTiles(
            _ => true, null, 40, 40, field, 12345, region, "t", 15, 4);
        var second = RegionSpawnPlanner.DeriveSpawnTiles(
            _ => true, null, 40, 40, field, 12345, region, "t", 15, 4);

        Assert.NotEmpty(first);
        Assert.Equal(first, second);
    }

    // A different type id salts to a DIFFERENT draw sequence — two types in the same region never spawn on
    // identical tile sets (the "salt discipline" the derivation documents).
    [Fact]
    public void DeriveSpawnTiles_DifferentTypeId_ProducesDifferentTiles()
    {
        var region = Region("r", 1, 1, 38, 38);
        var field = RegionSpawnPlanner.ComputeRoadDistanceField(authoredMap: null, width: 40, height: 40);

        var slime = RegionSpawnPlanner.DeriveSpawnTiles(
            _ => true, null, 40, 40, field, 999, region, "slime", 15, 4);
        var gnoll = RegionSpawnPlanner.DeriveSpawnTiles(
            _ => true, null, 40, 40, field, 999, region, "gnoll", 15, 4);

        Assert.NotEqual(slime, gnoll);
    }

    // §5.2 "in-region": every derived tile sits inside the authored rect (never a whisker outside it).
    [Fact]
    public void DeriveSpawnTiles_EveryTileIsInsideTheRegionRect()
    {
        var region = Region("r", 5, 5, 34, 34);
        var field = RegionSpawnPlanner.ComputeRoadDistanceField(authoredMap: null, width: 40, height: 40);

        var tiles = RegionSpawnPlanner.DeriveSpawnTiles(
            _ => true, null, 40, 40, field, 7, region, "t", 20, 4);

        Assert.NotEmpty(tiles);
        Assert.All(tiles, tile =>
        {
            Assert.InRange(tile.X, region.MinX, region.MaxX);
            Assert.InRange(tile.Y, region.MinY, region.MaxY);
        });
    }

    // §5.2 "min-spaced": every pair of derived tiles is at least minSpacing apart (Chebyshev, matching
    // WeightedScatter's own spacing rule).
    [Fact]
    public void DeriveSpawnTiles_EveryPairRespectsMinSpacing()
    {
        var region = Region("r", 1, 1, 38, 38);
        var field = RegionSpawnPlanner.ComputeRoadDistanceField(authoredMap: null, width: 40, height: 40);
        const int minSpacing = 4;

        var tiles = RegionSpawnPlanner.DeriveSpawnTiles(
            _ => true, null, 40, 40, field, 42, region, "t", 25, minSpacing);

        Assert.NotEmpty(tiles);
        for (var i = 0; i < tiles.Count; i++)
        {
            for (var j = i + 1; j < tiles.Count; j++)
            {
                Assert.True(
                    ChebyshevDistance(tiles[i], tiles[j]) >= minSpacing,
                    $"tiles {tiles[i]} and {tiles[j]} are closer than minSpacing={minSpacing}.");
            }
        }
    }

    // §5.2 "on-grass" (category filters absolute, D5): on an authored map, EVERY derived tile is Grass — never a
    // dirt/cobble/water/blocked tile, even though the region rect straddles all four.
    [Fact]
    public void DeriveSpawnTiles_OnAuthoredMap_OnlyLandsOnGrass()
    {
        // A tiny synthetic authored map: a grass field with a dirt road stripe and a water pond running through
        // the SAME rect the region will cover, bordered by walls (the authoring contract requires a full grid).
        var rows = new[]
        {
            "##############################",
            "#............,,,,,,~~~~......#",
            "#............,,,,,,~~~~......#",
            "#............,,,,,,~~~~......#",
            "#............,,,,,,~~~~......#",
            "#............,,,,,,~~~~......#",
            "#............,,,,,,~~~~......#",
            "#............,,,,,,~~~~......#",
            "##############################",
        };
        var map = AuthoredMap.Parse(rows);
        var region = Region("r", 1, 1, 28, 7);
        var field = RegionSpawnPlanner.ComputeRoadDistanceField(map, map.Width, map.Height);

        var tiles = RegionSpawnPlanner.DeriveSpawnTiles(
            map.IsWalkable, map, map.Width, map.Height, field, 3, region, "t", 30, 1);

        Assert.NotEmpty(tiles);
        Assert.All(tiles, tile => Assert.Equal(SurfaceCategory.Grass, map.CategoryAt(tile)));
    }

    // §5.2 degrade-gracefully: a region rect entirely outside the zone (e.g. the starter regions evaluated
    // against a small 64x64 test zone) derives ZERO tiles instead of throwing.
    [Fact]
    public void DeriveSpawnTiles_RegionEntirelyOutsideZone_ReturnsEmpty()
    {
        var region = Region("r", 120, 120, 220, 220); // the real slime_hollow rect
        var field = RegionSpawnPlanner.ComputeRoadDistanceField(authoredMap: null, width: 64, height: 64);

        var tiles = RegionSpawnPlanner.DeriveSpawnTiles(
            _ => true, null, 64, 64, field, 1, region, "t", 15, 4);

        Assert.Empty(tiles);
    }

    // A candidate predicate that rejects everything (e.g. an all-water/blocked map) also degrades to empty
    // rather than looping forever or throwing — WeightedScatter's own bounded attempt budget guarantees this.
    [Fact]
    public void DeriveSpawnTiles_NoWalkableCandidates_ReturnsEmpty()
    {
        var region = Region("r", 1, 1, 38, 38);
        var field = RegionSpawnPlanner.ComputeRoadDistanceField(authoredMap: null, width: 40, height: 40);

        var tiles = RegionSpawnPlanner.DeriveSpawnTiles(
            _ => false, null, 40, 40, field, 1, region, "t", 15, 4);

        Assert.Empty(tiles);
    }

    // §7/§8 "count: enough to host maxLive at overgrowth (ceil(1.5*maxLive) + slack)".
    [Theory]
    [InlineData(10, 19)]  // ceil(15) + 4
    [InlineData(8, 16)]   // ceil(12) + 4
    [InlineData(6, 13)]   // ceil(9) + 4
    [InlineData(1, 6)]    // ceil(1.5) + 4 = 2 + 4
    public void SpawnTileCountFor_MatchesOvergrownCapPlusSlack(int maxLive, int expected)
    {
        Assert.Equal(expected, RegionSpawnPlanner.SpawnTileCountFor(maxLive));
    }
}
