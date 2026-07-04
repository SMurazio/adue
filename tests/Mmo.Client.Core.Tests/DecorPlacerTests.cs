using System.Linq;
using Mmo.Client.Core;
using Mmo.Client.Core.Population;
using Mmo.Shared.Domain;
using Xunit;

namespace Mmo.Client.Core.Tests;

// PROCEDURAL-POPULATION P2 (docs/procedural-population-design.md D1 L1): pins DecorPlacer's determinism
// contract, the absolute category filter (D2 "base(category)" — cobble/stone/water/blocked NEVER carry
// decor), the near-road/far-road distribution shape (D2 distanceCurve, same acceptance style as P1's
// WeightedScatterTests), and the total instance budget (P2 §3, ≤30k across every class).
public sealed class DecorPlacerTests
{
    // Grass everywhere except a 2-wide dirt "road" column through the middle -- gives every grass AND
    // dirt class real candidate tiles, plus a road distance-field seed for the curve tests to bite on.
    private static AuthoredMap StripedRoadMap(int width, int height)
    {
        var mid = width / 2;
        var rows = new string[height];
        for (var y = 0; y < height; y++)
        {
            var chars = new char[width];
            for (var x = 0; x < width; x++)
            {
                chars[x] = x == mid || x == mid + 1 ? ',' : '.';
            }

            rows[y] = new string(chars);
        }

        return AuthoredMap.Parse(rows);
    }

    [Fact]
    public void SameSeed_ProducesIdenticalPlacements()
    {
        var map = StripedRoadMap(60, 60);

        var first = DecorPlacer.PlaceAll(map, zoneSeed: 4242);
        var second = DecorPlacer.PlaceAll(map, zoneSeed: 4242);

        foreach (var decorClass in DecorClassTable.Classes)
        {
            Assert.Equal(first[decorClass.Id], second[decorClass.Id]);
        }
    }

    [Fact]
    public void DifferentSeed_ProducesDifferentPlacements()
    {
        var map = StripedRoadMap(60, 60);

        var first = DecorPlacer.PlaceAll(map, zoneSeed: 4242);
        var second = DecorPlacer.PlaceAll(map, zoneSeed: 99);

        var anyDifferent = DecorClassTable.Classes
            .Any(decorClass => !first[decorClass.Id].SequenceEqual(second[decorClass.Id]));
        Assert.True(anyDifferent, "Two different zone seeds produced byte-identical decor across every class.");
    }

    [Fact]
    public void CategoryFilterIsAbsolute_NoDecorOffItsClassCategory()
    {
        // Five vertical stripes, one per SurfaceCategory: water, grass, dirt, cobble, dungeon stone. Every
        // grass-class instance must land on a grass-stripe tile; every dirt-class instance on a
        // dirt-stripe tile -- NEVER on cobble/stone/water/blocked, no matter how favorable the density
        // roll (WeightedScatter's isCandidate filter is absolute, P1 D3).
        const int stripeWidth = 24;
        const int height = 40;
        var stripeChars = new[] { '~', '.', ',', ':', '-' }; // water, grass, dirt, cobble, dungeon stone
        var width = stripeWidth * stripeChars.Length;

        var rows = new string[height];
        for (var y = 0; y < height; y++)
        {
            var chars = new char[width];
            for (var x = 0; x < width; x++)
            {
                chars[x] = stripeChars[x / stripeWidth];
            }

            rows[y] = new string(chars);
        }

        var map = AuthoredMap.Parse(rows);
        var placements = DecorPlacer.PlaceAll(map, zoneSeed: 7);

        var anyPlaced = false;
        foreach (var decorClass in DecorClassTable.Classes)
        {
            foreach (var instance in placements[decorClass.Id])
            {
                anyPlaced = true;
                var tile = new TileCoord((int)MathF.Round(instance.X), (int)MathF.Round(instance.Z));
                Assert.True(map.IsWalkable(tile), $"{decorClass.Id} placed on non-walkable tile {tile}.");
                Assert.Equal(decorClass.Category, map.CategoryAt(tile));
            }
        }

        Assert.True(anyPlaced, "No decor placed anywhere -- the striped test map can't validate the category filter.");
    }

    [Fact]
    public void AwayFromRoadDensityCurve_GrassTuftSmallIsSparserNearRoadThanFar()
    {
        // Same shape as WeightedScatterTests.AwayFromRoadDensityCurve_..., but end-to-end through
        // DecorPlacer + an AuthoredMap: a 100x60 all-grass map with a 2-wide dirt road down the middle
        // (x = 49/50). grass_tuft_small's own RoadFalloffTiles (10) should read clearly at this scale.
        const int width = 100;
        const int height = 60;
        var map = StripedRoadMap(width, height);

        var placements = DecorPlacer.PlaceAll(map, zoneSeed: 314);
        var tuftX = placements["grass_tuft_small"].Select(i => (int)MathF.Round(i.X)).ToList();

        Assert.True(tuftX.Count > 300, $"Too few placements ({tuftX.Count}) to judge distribution.");

        var nearCount = tuftX.Count(x => x is >= 45 and <= 54);
        var farCount = tuftX.Count(x => x <= 24 || x >= 75);

        var nearArea = 10 * height;
        var farArea = 50 * height;

        var nearDensity = (double)nearCount / nearArea;
        var farDensity = (double)farCount / farArea;

        Assert.True(
            nearDensity < farDensity,
            $"Expected near-road density ({nearDensity:F4}) to be strictly less than far-road density ({farDensity:F4}).");
    }

    [Fact]
    public void TotalInstancesAcrossEveryClass_NeverExceedsThe30kBudget()
    {
        // A generous mixed grass/dirt map (top half grass, bottom half dirt) so every class has real
        // candidate area, then the hard §3 perf ceiling: total placed instances across ALL FIVE classes
        // together must never exceed 30,000, regardless of how favorable the map is.
        const int width = 180;
        const int height = 160;
        var rows = new string[height];
        for (var y = 0; y < height; y++)
        {
            rows[y] = new string(y < height / 2 ? '.' : ',', width);
        }

        var map = AuthoredMap.Parse(rows);
        var placements = DecorPlacer.PlaceAll(map, zoneSeed: 555);

        var total = placements.Values.Sum(list => list.Count);
        Assert.True(total <= 30_000, $"DecorPlacer placed {total} instances total, over the P2 §3 30k budget.");
        Assert.True(total > 0, "DecorPlacer placed nothing on a generous mixed grass/dirt map.");
    }

    [Fact]
    public void PlacementInstances_PackIntoABufferWithMatchingOriginsAndLength()
    {
        // Placement -> buffer packing sanity (M2 path reuse): every DecorInstance this module produces
        // must round-trip through MultiMeshTileBuffer.PackDecorTransforms with the exact origin it was
        // placed at, and the buffer length must be exactly instanceCount * FloatsPerInstance. A generously
        // sized road (pebble's OWN category IS the road-seed category, so its distanceCurve always evaluates
        // at distance 0 -- RoadSuppression 0.30 applies as a flat multiplier everywhere for this class, by
        // design; the map needs enough dirt tiles for that lower density to still yield a non-empty sample).
        var map = StripedRoadMap(200, 200);
        var placements = DecorPlacer.PlaceAll(map, zoneSeed: 88);
        var instances = placements["pebble"];
        Assert.NotEmpty(instances);

        var buffer = MultiMeshTileBuffer.PackDecorTransforms(instances, groundY: 0.032f);

        Assert.Equal(instances.Count * MultiMeshTileBuffer.FloatsPerInstance, buffer.Length);

        for (var i = 0; i < instances.Count; i++)
        {
            var o = i * MultiMeshTileBuffer.FloatsPerInstance;
            Assert.Equal(instances[i].X, buffer[o + 3]);
            Assert.Equal(0.032f, buffer[o + 7]);
            Assert.Equal(instances[i].Z, buffer[o + 11]);
        }
    }
}
