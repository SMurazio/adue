using Mmo.Server.Runtime;
using Xunit;

namespace Mmo.Server.Tests;

// DUO-WAVE2 (exp/duo-abilities): the shared "two press ticks -> tier" classifier pinned at the EXACT window
// boundaries (Perfect <= 2 ticks, Good <= 6 ticks, else None). Order-independent (either press may be later).
public sealed class PairedTimingWindowTests
{
    private const uint Perfect = 2;
    private const uint Good = 6;

    [Theory]
    [InlineData(100u, 100u, PairTier.Perfect)] // simultaneous
    [InlineData(100u, 102u, PairTier.Perfect)] // exactly on the Perfect boundary (delta 2)
    [InlineData(102u, 100u, PairTier.Perfect)] // reversed order — same delta
    [InlineData(100u, 103u, PairTier.Good)]    // just past Perfect (delta 3)
    [InlineData(100u, 106u, PairTier.Good)]    // exactly on the Good boundary (delta 6)
    [InlineData(106u, 100u, PairTier.Good)]    // reversed order
    [InlineData(100u, 107u, PairTier.None)]    // just past Good (delta 7)
    [InlineData(0u, 1000u, PairTier.None)]     // far apart
    public void Classify_AtBoundaries(uint a, uint b, PairTier expected)
    {
        Assert.Equal(expected, PairedTimingWindow.Classify(a, b, Perfect, Good));
    }
}
