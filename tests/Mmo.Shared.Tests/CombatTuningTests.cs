using Mmo.Shared.Domain;
using Xunit;

namespace Mmo.Shared.Tests;

// COMBAT-TUNING: the ms->ticks conversions that keep the server (RootTicks(tickRate, rootMs)) and the client
// predictor (RootTicksFromTickMs(tickMs, rootMs)) computing the IDENTICAL swing-root window off the same replicated
// rootMs. The new rootMs-parameterized overloads must (a) agree with each other at a given cadence, (b) reduce to
// the old constant-based overloads when rootMs == MovementRootMs (parity preserved), and (c) floor to >= 1 tick.
public sealed class CombatTuningTests
{
    [Theory]
    [InlineData(20, 200)]
    [InlineData(20, 350)]
    [InlineData(30, 100)]
    [InlineData(60, 16)]
    public void RootTicksOverloadsAgreeAtCadence(int tickRate, int rootMs)
    {
        var tickMs = 1000d / tickRate;

        var fromRate = CombatTuning.RootTicks(tickRate, rootMs);
        var fromTickMs = CombatTuning.RootTicksFromTickMs(tickMs, rootMs);

        Assert.Equal(fromRate, fromTickMs);
    }

    [Fact]
    public void DefaultRootMsMatchesConstantOverloads()
    {
        // Passing MovementRootMs explicitly reproduces the original constant-based overloads byte-for-byte, so the
        // default behaviour is unchanged when the panel has not retuned rootMs.
        const int tickRate = 20;
        var tickMs = 1000d / tickRate;

        Assert.Equal(CombatTuning.RootTicks(tickRate), CombatTuning.RootTicks(tickRate, CombatTuning.MovementRootMs));
        Assert.Equal(CombatTuning.RootTicksFromTickMs(tickMs), CombatTuning.RootTicksFromTickMs(tickMs, CombatTuning.MovementRootMs));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-50)]
    public void RootTicksIsZeroForNonPositiveRootMs(int rootMs)
    {
        // rootMs 0 (the new default) = NO root; a negative value clamps to 0 the same way.
        Assert.Equal(0u, CombatTuning.RootTicks(20, rootMs));
        Assert.Equal(0u, CombatTuning.RootTicksFromTickMs(50d, rootMs));
    }

    [Fact]
    public void RootTicksRoundsUpForPositiveRootMs()
    {
        // Any positive rootMs still rounds UP to at least one tick (a configured root is never silently 0).
        Assert.True(CombatTuning.RootTicks(20, 1) >= 1u);
        Assert.True(CombatTuning.RootTicksFromTickMs(50d, 1) >= 1u);
    }

    [Fact]
    public void SnapshotComputesHalfAngleRadians()
    {
        var snapshot = new CombatTuningSnapshot(600, 200, 90d, 1.6d, 20);

        Assert.Equal(System.Math.PI / 2d, snapshot.HalfAngleRadians, 9);
    }
}
