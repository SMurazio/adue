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

    // P2-FLAGGED PERF FIX (spatial-hash spacing index): the two pins below guard the rewrite of the
    // O(placements) linear spacing scan into the 9-bucket spatial hash.
    // (1) EQUIVALENCE: on a small grid the hash-backed scatter must be BYTE-IDENTICAL to a brute-force
    //     reference reimplementation of the original algorithm (same PRNG type, same draw order, same
    //     linear spacing scan) -- the fix's whole correctness argument is "only the lookup structure
    //     changed", and this test is that argument made executable.
    // (2) SCALE: a P2-sized scatter (10k target on the real 384 grid, spacing 2) completes and never
    //     violates the spacing invariant -- with the old linear scan this call was the potential
    //     multi-second client login stall; with the hash it is O(attempts).
    [Fact]
    public void SpatialHashSpacing_IsByteIdenticalToBruteForceReference()
    {
        const int size = 48;
        const int spacing = 3;
        foreach (var seed in new[] { 1, 77, 90210 })
        {
            var actual = WeightedScatter.Scatter(
                size, size, seed, AlwaysCandidate, HalfDensity, targetCount: 60, minSpacing: spacing);
            var expected = BruteForceReferenceScatter(
                size, size, seed, AlwaysCandidate, HalfDensity, targetCount: 60, minSpacing: spacing);
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void LargeScatter_CompletesWithSpacingIntact()
    {
        var placements = WeightedScatter.Scatter(
            384, 384, seed: 4242, AlwaysCandidate, FullDensity, targetCount: 10_000, minSpacing: 2);

        // Density 1 + spacing 2 on a 384 grid: the budget bounds attempts, so we just require a healthy
        // yield (a quarter of the theoretical ~192^2/... packing is far above any regression noise floor).
        Assert.True(placements.Count > 2_500, $"only {placements.Count} placements");
        var seen = new HashSet<TileCoord>(placements);
        Assert.Equal(placements.Count, seen.Count);
        AssertSpacingInvariant(placements, 2);
    }

    // The original pre-fix algorithm, reimplemented verbatim as the equivalence oracle: same SplitMix64
    // seeding, same draw order (x, y, unconditional density roll), same used-set semantics, and the
    // ORIGINAL O(n) linear spacing scan. SplitMix64 is internal to Mmo.Shared (no InternalsVisibleTo), so
    // the oracle carries its own bit-identical copy of the published algorithm below (OracleSeedState /
    // OracleNextInt / OracleNextDouble) — matching the production constants exactly IS the point: a drift
    // in either copy fails the equivalence test loudly.
    private static List<TileCoord> BruteForceReferenceScatter(
        int width, int height, int seed,
        Func<TileCoord, bool> isCandidate, Func<TileCoord, double> density,
        int targetCount, int minSpacing)
    {
        var placements = new List<TileCoord>();
        var used = new HashSet<TileCoord>();
        var budget = (targetCount * 64L) + 1024L;
        var state = OracleSeedState(seed);
        for (var attempt = 0L; attempt < budget && placements.Count < targetCount; attempt++)
        {
            var x = OracleNextInt(ref state, width);
            var y = OracleNextInt(ref state, height);
            var tile = new TileCoord(x, y);
            if (used.Contains(tile) || !isCandidate(tile))
            {
                continue;
            }

            var roll = OracleNextDouble(ref state);
            if (roll >= density(tile))
            {
                continue;
            }

            var farEnough = true;
            foreach (var placed in placements)
            {
                if (Math.Max(Math.Abs(placed.X - tile.X), Math.Abs(placed.Y - tile.Y)) < minSpacing)
                {
                    farEnough = false;
                    break;
                }
            }

            if (!farEnough)
            {
                continue;
            }

            used.Add(tile);
            placements.Add(tile);
        }

        return placements;
    }

    private static double HalfDensity(TileCoord _)
    {
        return 0.5;
    }

    // Bit-identical copies of Mmo.Shared's internal SplitMix64 (see BruteForceReferenceScatter's comment).
    private static ulong OracleSeedState(int seed) => (ulong)(uint)seed * 0x9E3779B97F4A7C15UL;

    private static ulong OracleNext(ref ulong state)
    {
        unchecked
        {
            state += 0x9E3779B97F4A7C15UL;
            var z = state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }

    private static double OracleNextDouble(ref ulong state) => (OracleNext(ref state) >> 11) * (1.0 / (1UL << 53));

    private static int OracleNextInt(ref ulong state, int exclusiveMax) => (int)(OracleNext(ref state) % (ulong)exclusiveMax);

    private static void AssertSpacingInvariant(IReadOnlyList<TileCoord> placements, int minSpacing)
    {
        // O(n) via the same bucketing idea the production index uses -- the test must not itself be the
        // quadratic thing the fix removed.
        var buckets = new Dictionary<(int, int), List<TileCoord>>();
        foreach (var tile in placements)
        {
            var key = (tile.X / minSpacing, tile.Y / minSpacing);
            for (var nx = key.Item1 - 1; nx <= key.Item1 + 1; nx++)
            {
                for (var ny = key.Item2 - 1; ny <= key.Item2 + 1; ny++)
                {
                    if (!buckets.TryGetValue((nx, ny), out var bucket))
                    {
                        continue;
                    }

                    foreach (var other in bucket)
                    {
                        var violation = Math.Max(Math.Abs(other.X - tile.X), Math.Abs(other.Y - tile.Y)) < minSpacing;
                        Assert.False(violation, $"spacing violation: {other} vs {tile}");
                    }
                }
            }

            if (!buckets.TryGetValue(key, out var own))
            {
                own = new List<TileCoord>();
                buckets[key] = own;
            }

            own.Add(tile);
        }
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
