using Mmo.Shared.Domain;
using Mmo.Shared.Domain.Population;
using Xunit;

namespace Mmo.Shared.Tests;

// PROCEDURAL-POPULATION P1 (docs/procedural-population-design.md D2 "distanceCurve"): pins the BFS
// distance transform's correctness on small hand-checkable grids, plus the determinism contract every
// generator in this repo carries (same inputs -> byte-identical output).
public sealed class TileDistanceFieldTests
{
    [Fact]
    public void SeedTilesHaveDistanceZero()
    {
        var field = TileDistanceField.Compute(5, 5, new[] { new TileCoord(2, 2) });

        Assert.Equal(0, field.DistanceAt(2, 2));
    }

    [Fact]
    public void DistanceGrowsByOnePerFourNeighborStep_OnAHandGrid()
    {
        // Single seed at the center of a 5x5 grid: Manhattan distance is exact for a single 4-neighbor
        // BFS source with no obstacles (the whole grid is open, so BFS distance == Manhattan distance).
        var field = TileDistanceField.Compute(5, 5, new[] { new TileCoord(2, 2) });

        Assert.Equal(0, field.DistanceAt(2, 2));
        Assert.Equal(1, field.DistanceAt(3, 2));
        Assert.Equal(1, field.DistanceAt(2, 1));
        Assert.Equal(2, field.DistanceAt(4, 2));
        Assert.Equal(2, field.DistanceAt(3, 1)); // diagonal neighbor: 1 + 1, not sqrt(2) -- this is grid geometry.
        Assert.Equal(4, field.DistanceAt(0, 0)); // corner: |2-0| + |2-0| = 4.
        Assert.Equal(4, field.DistanceAt(4, 4));
    }

    [Fact]
    public void MultipleSeedsTakeTheNearestDistance()
    {
        // Seeds at both ends of a row: every tile takes the distance to whichever seed is closer.
        var field = TileDistanceField.Compute(9, 1, new[] { new TileCoord(0, 0), new TileCoord(8, 0) });

        Assert.Equal(0, field.DistanceAt(0, 0));
        Assert.Equal(0, field.DistanceAt(8, 0));
        Assert.Equal(4, field.DistanceAt(4, 0)); // exact midpoint -- 4 from either end.
        Assert.Equal(1, field.DistanceAt(1, 0));
        Assert.Equal(1, field.DistanceAt(7, 0));
    }

    [Fact]
    public void BlockedTilesStillGetAGeometricDistance()
    {
        // DECISION (documented in TileDistanceField's summary): the transform ignores walkability
        // entirely. There is no concept of "blocked" here at all -- this test simply confirms every tile
        // in the grid gets a real, finite distance regardless of what a caller's isCandidate predicate
        // might separately consider blocked; the transform itself never consults any blocked-set.
        var field = TileDistanceField.Compute(10, 10, new[] { new TileCoord(0, 0) });

        for (var y = 0; y < 10; y++)
        {
            for (var x = 0; x < 10; x++)
            {
                Assert.Equal(x + y, field.DistanceAt(x, y));
            }
        }
    }

    [Fact]
    public void NoSeeds_EveryTileIsMaxValue()
    {
        var field = TileDistanceField.Compute(4, 4, Array.Empty<TileCoord>());

        for (var y = 0; y < 4; y++)
        {
            for (var x = 0; x < 4; x++)
            {
                Assert.Equal(int.MaxValue, field.DistanceAt(x, y));
            }
        }
    }

    [Fact]
    public void OutOfBoundsSeedsAreIgnoredNotThrown()
    {
        var field = TileDistanceField.Compute(4, 4, new[] { new TileCoord(-1, -1), new TileCoord(2, 2) });

        Assert.Equal(0, field.DistanceAt(2, 2));
    }

    [Fact]
    public void DistanceAtOutOfBounds_Throws()
    {
        var field = TileDistanceField.Compute(4, 4, new[] { new TileCoord(0, 0) });

        Assert.Throws<ArgumentOutOfRangeException>(() => field.DistanceAt(4, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => field.DistanceAt(0, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => field.DistanceAt(-1, 0));
    }

    [Fact]
    public void ComputeIsDeterministicAcrossCalls()
    {
        var seeds = new[] { new TileCoord(0, 0), new TileCoord(63, 63), new TileCoord(30, 10) };

        var first = TileDistanceField.Compute(64, 64, seeds);
        var second = TileDistanceField.Compute(64, 64, seeds);

        for (var y = 0; y < 64; y++)
        {
            for (var x = 0; x < 64; x++)
            {
                Assert.Equal(first.DistanceAt(x, y), second.DistanceAt(x, y));
            }
        }
    }
}
