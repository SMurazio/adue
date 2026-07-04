using Mmo.Client.Core;
using Mmo.Shared.Domain;
using Xunit;

namespace Mmo.Client.Core.Tests;

// ECOLOGY E4 (docs/ecology-v1-design.md D6a, §5.4): the minimap's region-shading color TABLE, pinned headlessly
// (no Godot) — the acceptance criterion's "minimap state-mapping unit test".
public sealed class MinimapEcologyOverlayTests
{
    [Fact]
    public void HealthyHasNoOverlayInk()
    {
        var (_, _, _, a) = MinimapEcologyOverlay.ColorFor(EcologyPopulationState.Healthy);
        Assert.Equal(0, a); // fully transparent — the unremarkable baseline earns no screen ink.
    }

    [Theory]
    [InlineData(EcologyPopulationState.Depleted)]
    [InlineData(EcologyPopulationState.Thin)]
    [InlineData(EcologyPopulationState.Rich)]
    [InlineData(EcologyPopulationState.Overgrown)]
    public void DeviatingStatesAreVisible(EcologyPopulationState state)
    {
        var (_, _, _, a) = MinimapEcologyOverlay.ColorFor(state);
        Assert.True(a > 0, $"{state} should paint SOME overlay ink.");
    }

    [Fact]
    public void DepletedReadsRed()
    {
        var (r, g, b, _) = MinimapEcologyOverlay.ColorFor(EcologyPopulationState.Depleted);
        Assert.True(r > g && r > b, "Depleted should be red-dominant.");
    }

    [Fact]
    public void ThinReadsAmber()
    {
        var (r, g, b, _) = MinimapEcologyOverlay.ColorFor(EcologyPopulationState.Thin);
        Assert.True(r > b, "Thin (amber) should have more red than blue.");
        Assert.True(g > b, "Thin (amber) should have more green than blue.");
    }

    [Fact]
    public void RichReadsGreen()
    {
        var (r, g, b, _) = MinimapEcologyOverlay.ColorFor(EcologyPopulationState.Rich);
        Assert.True(g > r && g > b, "Rich should be green-dominant.");
    }

    [Fact]
    public void OvergrownReadsViolet()
    {
        var (r, g, b, _) = MinimapEcologyOverlay.ColorFor(EcologyPopulationState.Overgrown);
        Assert.True(r > g && b > g, "Overgrown (violet) should have red AND blue exceeding green.");
    }

    [Fact]
    public void EveryStateHasADistinctColor()
    {
        var colors = new HashSet<(byte, byte, byte, byte)>
        {
            MinimapEcologyOverlay.ColorFor(EcologyPopulationState.Depleted),
            MinimapEcologyOverlay.ColorFor(EcologyPopulationState.Thin),
            MinimapEcologyOverlay.ColorFor(EcologyPopulationState.Healthy),
            MinimapEcologyOverlay.ColorFor(EcologyPopulationState.Rich),
            MinimapEcologyOverlay.ColorFor(EcologyPopulationState.Overgrown),
        };

        Assert.Equal(5, colors.Count);
    }

    [Fact]
    public void WorstColorFor_DelegatesToTheSharedSeverityRule()
    {
        // A region with Healthy + Depleted types must shade as Depleted (EcologyLegibility.WorstOf), not Healthy —
        // the SAME rule the server's /rumors line uses, so the minimap and /rumors never disagree.
        var color = MinimapEcologyOverlay.WorstColorFor(new[]
        {
            EcologyPopulationState.Healthy,
            EcologyPopulationState.Depleted,
        });

        Assert.Equal(MinimapEcologyOverlay.ColorFor(EcologyPopulationState.Depleted), color);
    }
}
