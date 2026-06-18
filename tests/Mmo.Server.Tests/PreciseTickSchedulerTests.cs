using Mmo.Server.Runtime;
using Xunit;

namespace Mmo.Server.Tests;

public sealed class PreciseTickSchedulerTests
{
    [Fact]
    public void CountDueTicksReturnsZeroBeforeDeadline()
    {
        Assert.Equal(0, PreciseTickScheduler.CountDueTicks(
            nowTimestamp: 999,
            nextTickTimestamp: 1000,
            tickIntervalTimestampTicks: 50));
    }

    [Fact]
    public void CountDueTicksIncludesCatchUpTicks()
    {
        Assert.Equal(1, PreciseTickScheduler.CountDueTicks(
            nowTimestamp: 1000,
            nextTickTimestamp: 1000,
            tickIntervalTimestampTicks: 50));
        Assert.Equal(3, PreciseTickScheduler.CountDueTicks(
            nowTimestamp: 1100,
            nextTickTimestamp: 1000,
            tickIntervalTimestampTicks: 50));
    }

    [Fact]
    public void CalculateDelayBeforeDeadlineCapsToPollDelayAndReservesSpinWindow()
    {
        var delay = PreciseTickScheduler.CalculateDelayBeforeDeadline(
            nowTimestamp: 0,
            deadlineTimestamp: StopwatchTicks(TimeSpan.FromMilliseconds(50)),
            spinThreshold: TimeSpan.FromMilliseconds(1.5d),
            maxDelay: TimeSpan.FromMilliseconds(2d));

        Assert.Equal(TimeSpan.FromMilliseconds(2d), delay);
    }

    [Fact]
    public void CalculateDelayBeforeDeadlineReturnsZeroInsideSpinWindow()
    {
        var delay = PreciseTickScheduler.CalculateDelayBeforeDeadline(
            nowTimestamp: StopwatchTicks(TimeSpan.FromMilliseconds(49)),
            deadlineTimestamp: StopwatchTicks(TimeSpan.FromMilliseconds(50)),
            spinThreshold: TimeSpan.FromMilliseconds(1.5d),
            maxDelay: TimeSpan.FromMilliseconds(2d));

        Assert.Equal(TimeSpan.Zero, delay);
    }

    private static long StopwatchTicks(TimeSpan value)
    {
        return (long)Math.Round(value.TotalSeconds * System.Diagnostics.Stopwatch.Frequency);
    }
}
