using Mmo.Shared.Domain;
using Mmo.Shared.Domain.Population;
using Xunit;

namespace Mmo.Shared.Tests;

// PROCEDURAL-POPULATION P1 (docs/procedural-population-design.md D3): pins the weighted rejection
// sampler's determinism contract, the absolute category filter, min-spacing, and the distribution-sanity
// acceptance criterion called out explicitly by the design doc's P1 task: with a road down the middle and
// an away-from-road density curve, near-road density must be strictly less than far-road density over a
// big sample.
public sealed class WeightedScatterTests
{
    private static bool AlwaysCandidate(TileCoord _)
    {
        return true;
    }

    private static double FullDensity(TileCoord _)
    {
        return 1.0;
    }

    [Fact]
    public void SameSeed_ProducesIdenticalPlacements()
    {
        var first = WeightedScatter.Scatter(64, 64, seed: 123, AlwaysCandidate, FullDensity, targetCount: 50, minSpacing: 2);
        var second = WeightedScatter.Scatter(64, 64, seed: 123, AlwaysCandidate, FullDensity, targetCount: 50, minSpacing: 2);

        Assert.Equal(first, second);
    }

    [Fact]
    public void DifferentSeed_ProducesDifferentPlacements()
    {
        var first = WeightedScatter.Scatter(64, 64, seed: 123, AlwaysCandidate, FullDensity, targetCount: 50, minSpacing: 2);
        var second = WeightedScatter.Scatter(64, 64, seed: 456, AlwaysCandidate, FullDensity, targetCount: 50, minSpacing: 2);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void ReachesTargetCount_WhenDensityAndSpacingAllowIt()
    {
        // A generous 64x64 grid, full density, modest spacing -- the target should be fully reachable.
        var placements = WeightedScatter.Scatter(64, 64, seed: 1, AlwaysCandidate, FullDensity, targetCount: 50, minSpacing: 2);

        Assert.Equal(50, placements.Count);
    }

    [Fact]
    public void MinSpacingIsNeverViolated()
    {
        var placements = WeightedScatter.Scatter(48, 48, seed: 5, AlwaysCandidate, FullDensity, targetCount: 60, minSpacing: 3);

        for (var i = 0; i < placements.Count; i++)
        {
            for (var j = i + 1; j < placements.Count; j++)
            {
                var dx = Math.Abs(placements[i].X - placements[j].X);
                var dy = Math.Abs(placements[i].Y - placements[j].Y);
                var chebyshev = Math.Max(dx, dy);
                Assert.True(chebyshev >= 3, $"Placements {placements[i]} and {placements[j]} are only {chebyshev} apart (minSpacing 3).");
            }
        }
    }

    [Fact]
    public void CategoryFilterIsAbsolute_ZeroSamplesOnFilteredTiles()
    {
        // isCandidate rejects every tile with an odd X -- density is irrelevant, no placement should ever
        // land on an odd-X tile no matter how many attempts are available.
        var placements = WeightedScatter.Scatter(
            64,
            64,
            seed: 9,
            isCandidate: tile => tile.X % 2 == 0,
            density: FullDensity,
            targetCount: 200,
            minSpacing: 1,
            maxAttempts: 200_000);

        Assert.NotEmpty(placements);
        Assert.All(placements, tile => Assert.Equal(0, tile.X % 2));
    }

    [Fact]
    public void ZeroDensity_NeverPlacesAnything()
    {
        var placements = WeightedScatter.Scatter(32, 32, seed: 2, AlwaysCandidate, density: _ => 0.0, targetCount: 20, minSpacing: 1, maxAttempts: 5000);

        Assert.Empty(placements);
    }

    [Fact]
    public void NonPositiveTargetCount_ReturnsEmpty()
    {
        var placements = WeightedScatter.Scatter(16, 16, seed: 1, AlwaysCandidate, FullDensity, targetCount: 0, minSpacing: 1);

        Assert.Empty(placements);
    }

    [Fact]
    public void PreclaimedTilesAreNeverPlacedOn()
    {
        var preclaimed = new[] { new TileCoord(5, 5), new TileCoord(6, 6) };

        var placements = WeightedScatter.Scatter(
            16,
            16,
            seed: 3,
            AlwaysCandidate,
            FullDensity,
            targetCount: 100,
            minSpacing: 1,
            preclaimed: preclaimed,
            maxAttempts: 20_000);

        Assert.DoesNotContain(new TileCoord(5, 5), placements);
        Assert.DoesNotContain(new TileCoord(6, 6), placements);
    }

    [Fact]
    public void InvalidArguments_Throw()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => WeightedScatter.Scatter(0, 10, 1, AlwaysCandidate, FullDensity, 5, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => WeightedScatter.Scatter(10, 0, 1, AlwaysCandidate, FullDensity, 5, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => WeightedScatter.Scatter(10, 10, 1, AlwaysCandidate, FullDensity, 5, 0));
        Assert.Throws<ArgumentNullException>(() => WeightedScatter.Scatter(10, 10, 1, null!, FullDensity, 5, 1));
        Assert.Throws<ArgumentNullException>(() => WeightedScatter.Scatter(10, 10, 1, AlwaysCandidate, null!, 5, 1));
    }

    // ---- Distribution sanity (the design doc's explicit P1 acceptance criterion) ----------------------

    [Fact]
    public void AwayFromRoadDensityCurve_PlacesFewerTilesNearRoadThanFar()
    {
        // A 100-wide, 60-tall map with a "road" seeded down the middle column (x = 49/50). Distance
        // field is BFS distance from that column. density(tile) grows with distance-to-road (civilization
        // suppresses wilderness, design D2) up to a cap, so the near-road band should carry a visibly
        // lower placement density than the far band -- exactly the acceptance check the P1 task names.
        const int width = 100;
        const int height = 60;
        var roadSeeds = new List<TileCoord>();
        for (var y = 0; y < height; y++)
        {
            roadSeeds.Add(new TileCoord(49, y));
            roadSeeds.Add(new TileCoord(50, y));
        }

        var field = TileDistanceField.Compute(width, height, roadSeeds);

        // distanceCurve(d): 0 at the road, ramping linearly to 1.0 at 20+ tiles away -- deliberately
        // simple and monotonic, matching D2's "thin near roads, thicken far away" shape.
        double DistanceCurve(TileCoord tile)
        {
            var d = field.DistanceAt(tile);
            return Math.Min(1.0, d / 20.0);
        }

        // A dense uniform target so the rejection sampler has plenty of headroom to reflect the density
        // field's shape rather than being attempt-budget-starved.
        var placements = WeightedScatter.Scatter(
            width,
            height,
            seed: 77,
            isCandidate: AlwaysCandidate,
            density: DistanceCurve,
            targetCount: 2000,
            minSpacing: 1,
            maxAttempts: 2_000_000);

        Assert.True(placements.Count > 500, $"Too few placements ({placements.Count}) to judge distribution.");

        // Near band: within 5 tiles of the road (x in [45, 54]). Far band: at least 25 tiles away
        // (x <= 24 or x >= 75). Compare placement DENSITY (count / band area), not raw count, since the
        // two bands are different sizes.
        var nearCount = placements.Count(t => t.X is >= 45 and <= 54);
        var farCount = placements.Count(t => t.X <= 24 || t.X >= 75);

        var nearArea = 10 * height;
        var farArea = 50 * height;

        var nearDensity = (double)nearCount / nearArea;
        var farDensity = (double)farCount / farArea;

        Assert.True(
            nearDensity < farDensity,
            $"Expected near-road density ({nearDensity:F4}, {nearCount} in {nearArea} tiles) to be strictly less than " +
            $"far-road density ({farDensity:F4}, {farCount} in {farArea} tiles).");
    }
}
