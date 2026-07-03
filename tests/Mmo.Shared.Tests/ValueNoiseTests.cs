using Mmo.Shared.Domain.Population;
using Xunit;

namespace Mmo.Shared.Tests;

// PROCEDURAL-POPULATION P1 (docs/procedural-population-design.md D2 "patchNoise"): pins the value-noise
// determinism contract (same seed -> identical field, different seed -> a different one) and the [0, 1]
// output range across a dense sample grid.
public sealed class ValueNoiseTests
{
    [Fact]
    public void SampleIsAlwaysInZeroToOneRange()
    {
        for (var x = 0; x < 200; x++)
        {
            for (var y = 0; y < 200; y++)
            {
                var value = ValueNoise.Sample(seed: 42, x, y, cellScale: 8.0);
                Assert.InRange(value, 0.0, 1.0);
            }
        }
    }

    [Fact]
    public void SampleIsDeterministic_SameSeedSameCoordSameValue()
    {
        for (var x = 0; x < 50; x += 3)
        {
            for (var y = 0; y < 50; y += 3)
            {
                var first = ValueNoise.Sample(7, x, y, 6.0);
                var second = ValueNoise.Sample(7, x, y, 6.0);
                Assert.Equal(first, second);
            }
        }
    }

    [Fact]
    public void DifferentSeeds_ProduceDifferentFields()
    {
        var differences = 0;
        for (var x = 0; x < 40; x++)
        {
            for (var y = 0; y < 40; y++)
            {
                var a = ValueNoise.Sample(1, x, y, 8.0);
                var b = ValueNoise.Sample(2, x, y, 8.0);
                if (Math.Abs(a - b) > 1e-9)
                {
                    differences++;
                }
            }
        }

        // A different seed must reshuffle the lattice -- virtually every sample should differ. Some
        // small number of accidental near-matches is fine; near-total identity would mean the seed isn't
        // actually wired into the hash.
        Assert.True(differences > 1500, $"Expected most of the 1600 samples to differ between seeds, got {differences}.");
    }

    [Fact]
    public void AdjacentTilesAreSmooth_NotWhiteNoise()
    {
        // The whole point of value noise over raw per-tile hashing is that NEIGHBORING tiles are close
        // in value (thickets/clearings), not uncorrelated. At a generous cellScale, step-to-step deltas
        // should be small almost everywhere -- assert the average adjacent delta is well under half the
        // full [0, 1] range, which a per-tile-independent hash (no interpolation) would blow past.
        const double cellScale = 12.0;
        double totalDelta = 0;
        var count = 0;
        double? previous = null;
        for (var x = 0; x < 100; x++)
        {
            var value = ValueNoise.Sample(3, x, 5, cellScale);
            if (previous is not null)
            {
                totalDelta += Math.Abs(value - previous.Value);
                count++;
            }

            previous = value;
        }

        var averageDelta = totalDelta / count;
        Assert.True(averageDelta < 0.15, $"Expected smooth step-to-step noise, got average adjacent delta {averageDelta}.");
    }

    [Fact]
    public void InvalidCellScale_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ValueNoise.Sample(1, 0, 0, 0.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ValueNoise.Sample(1, 0, 0, -3.0));
    }
}
