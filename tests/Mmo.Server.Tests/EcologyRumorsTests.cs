using Mmo.Server.Runtime;
using Mmo.Shared.Domain;
using Xunit;

namespace Mmo.Server.Tests;

// ECOLOGY E4 (docs/ecology-v1-design.md D6b/D6c, §5.4): the /rumors + login-rumor flavor-text TABLE, pinned
// against the exact strings the task authored (write-once-not-inline discipline). EcologyRumors is internal to
// Mmo.Server — reachable here via [InternalsVisibleTo("Mmo.Server.Tests")].
public sealed class EcologyRumorsTests
{
    [Theory]
    [InlineData(EcologyPopulationState.Depleted, "Slime Hollow has been hunted to the brink.")]
    [InlineData(EcologyPopulationState.Thin, "Game grows scarce in Slime Hollow.")]
    [InlineData(EcologyPopulationState.Healthy, "Slime Hollow teems with its usual life.")]
    [InlineData(EcologyPopulationState.Rich, "Slime Hollow flourishes.")]
    public void LineFor_MatchesTheAuthoredLineForEachState(EcologyPopulationState state, string expected)
    {
        Assert.Equal(expected, EcologyRumors.LineFor("Slime Hollow", state));
    }

    [Fact]
    public void LineFor_Overgrown_MentionsUnusualNumbersAndSize()
    {
        var line = EcologyRumors.LineFor("The Verge", EcologyPopulationState.Overgrown);

        Assert.StartsWith("The Verge is overrun", line);
        Assert.Contains("unusual numbers", line);
        Assert.Contains("size", line);
    }

    [Fact]
    public void LineFor_FormatsADifferentRegionNameCorrectly()
    {
        // The table is written ONCE and formatted per call — a different display name must thread through cleanly,
        // not get baked into a stale string from a previous call.
        Assert.Equal("Eastern Scrubland flourishes.", EcologyRumors.LineFor("Eastern Scrubland", EcologyPopulationState.Rich));
        Assert.Equal("Slime Hollow flourishes.", EcologyRumors.LineFor("Slime Hollow", EcologyPopulationState.Rich));
    }
}
