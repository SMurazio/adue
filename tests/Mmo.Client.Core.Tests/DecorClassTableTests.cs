using Mmo.Client.Core.Population;
using Xunit;

namespace Mmo.Client.Core.Tests;

// PROCEDURAL-POPULATION P2 (docs/procedural-population-design.md §3 perf posture): pins the class table's
// own budget contract directly, independent of any placement run — a future class addition (or a
// TargetCount bump) that blows the <=30k instance ceiling fails HERE, loudly, instead of only showing up
// as a frame-time regression discovered later.
public sealed class DecorClassTableTests
{
    [Fact]
    public void TotalTargetCountBudget_DoesNotExceed30000()
    {
        var total = 0;
        foreach (var decorClass in DecorClassTable.Classes)
        {
            total += decorClass.TargetCount;
        }

        Assert.True(total <= 30_000, $"DecorClassTable TargetCount sum is {total}, over the P2 §3 30k budget.");
    }

    [Fact]
    public void EveryClass_HasIdsUniqueAndSaneTunables()
    {
        var seenIds = new HashSet<string>();
        foreach (var decorClass in DecorClassTable.Classes)
        {
            Assert.True(seenIds.Add(decorClass.Id), $"Duplicate DecorClass.Id '{decorClass.Id}'.");
            Assert.True(decorClass.MinSpacing >= 1, $"{decorClass.Id}: MinSpacing must be >= 1.");
            Assert.True(decorClass.TargetCount > 0, $"{decorClass.Id}: TargetCount must be positive.");
            Assert.InRange(decorClass.BaseDensity, 0.0, 1.0);
            Assert.InRange(decorClass.RoadSuppression, 0.0, 1.0);
            Assert.True(decorClass.RoadFalloffTiles > 0, $"{decorClass.Id}: RoadFalloffTiles must be positive.");
            Assert.True(decorClass.NoiseCellScale > 0, $"{decorClass.Id}: NoiseCellScale must be positive.");
            Assert.True(decorClass.Width > 0 && decorClass.Height > 0, $"{decorClass.Id}: Width/Height must be positive.");
        }
    }
}
