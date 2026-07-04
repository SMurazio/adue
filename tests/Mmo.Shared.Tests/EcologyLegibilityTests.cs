using Mmo.Shared.Domain;
using Xunit;

namespace Mmo.Shared.Tests;

// ECOLOGY E4 (docs/ecology-v1-design.md D6a/D6b/D6c): the shared "which state governs a mixed region" rule, used
// by BOTH the minimap overlay color (MinimapEcologyOverlay) and the server's /rumors line (EcologyRumors) — pinned
// once here so the two legibility surfaces can never silently diverge.
public sealed class EcologyLegibilityTests
{
    [Fact]
    public void WorstOf_DepletedOutranksEveryOtherState()
    {
        Assert.Equal(
            EcologyPopulationState.Depleted,
            EcologyLegibility.WorstOf(new[]
            {
                EcologyPopulationState.Overgrown,
                EcologyPopulationState.Healthy,
                EcologyPopulationState.Depleted,
                EcologyPopulationState.Rich,
            }));
    }

    [Fact]
    public void WorstOf_OvergrownOutranksThinRichAndHealthy()
    {
        Assert.Equal(
            EcologyPopulationState.Overgrown,
            EcologyLegibility.WorstOf(new[]
            {
                EcologyPopulationState.Healthy,
                EcologyPopulationState.Rich,
                EcologyPopulationState.Thin,
                EcologyPopulationState.Overgrown,
            }));
    }

    [Fact]
    public void WorstOf_SingleHealthyEntryIsHealthy()
    {
        Assert.Equal(
            EcologyPopulationState.Healthy,
            EcologyLegibility.WorstOf(new[] { EcologyPopulationState.Healthy }));
    }

    [Fact]
    public void WorstOf_TiesResolveToTheFirstMatchingEntry()
    {
        // Two DEPLETED entries (the top severity rank) tie; the first one in the input order wins — this is the
        // "ties -> first" discipline the design calls for (D6c uses the analogous rule for the login rumor).
        var states = new[]
        {
            EcologyPopulationState.Depleted,
            EcologyPopulationState.Depleted,
        };

        Assert.Equal(EcologyPopulationState.Depleted, EcologyLegibility.WorstOf(states));
    }

    [Theory]
    [InlineData(EcologyPopulationState.Depleted, 2)]
    [InlineData(EcologyPopulationState.Thin, 1)]
    [InlineData(EcologyPopulationState.Healthy, 0)]
    [InlineData(EcologyPopulationState.Rich, 1)]
    [InlineData(EcologyPopulationState.Overgrown, 2)]
    public void DistanceFromHealthy_IsSymmetricAroundHealthy(EcologyPopulationState state, int expectedDistance)
    {
        // D6c: "max distance from Healthy in either direction" — Depleted and Overgrown are EQUALLY extreme
        // (distance 2), as are Thin and Rich (distance 1). This is deliberately different from WorstOf's
        // asymmetric severity order.
        Assert.Equal(expectedDistance, EcologyLegibility.DistanceFromHealthy(state));
    }
}
