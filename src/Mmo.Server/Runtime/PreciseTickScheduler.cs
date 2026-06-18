using System.Diagnostics;

namespace Mmo.Server.Runtime;

internal static class PreciseTickScheduler
{
    public static readonly TimeSpan DefaultSpinThreshold = TimeSpan.FromMilliseconds(1.5d);
    public static readonly TimeSpan DefaultMaxPollDelay = TimeSpan.FromMilliseconds(2d);

    public static long TickIntervalTimestampTicks(int tickRate)
    {
        return Math.Max(1, (long)Math.Round(Stopwatch.Frequency / (double)tickRate));
    }

    public static TimeSpan ToTimeSpan(long stopwatchTicks)
    {
        return TimeSpan.FromSeconds(stopwatchTicks / (double)Stopwatch.Frequency);
    }

    public static int CountDueTicks(long nowTimestamp, long nextTickTimestamp, long tickIntervalTimestampTicks)
    {
        if (tickIntervalTimestampTicks <= 0 || nowTimestamp < nextTickTimestamp)
        {
            return 0;
        }

        var elapsedTicks = nowTimestamp - nextTickTimestamp;
        return checked((int)(elapsedTicks / tickIntervalTimestampTicks) + 1);
    }

    public static TimeSpan CalculateDelayBeforeDeadline(
        long nowTimestamp,
        long deadlineTimestamp,
        TimeSpan spinThreshold,
        TimeSpan maxDelay)
    {
        if (nowTimestamp >= deadlineTimestamp)
        {
            return TimeSpan.Zero;
        }

        var remaining = ToTimeSpan(deadlineTimestamp - nowTimestamp);
        if (remaining <= spinThreshold)
        {
            return TimeSpan.Zero;
        }

        var delay = remaining - spinThreshold;
        return delay <= maxDelay ? delay : maxDelay;
    }

    public static async ValueTask WaitUntilNextTickOrPollAsync(long deadlineTimestamp, CancellationToken cancellationToken)
    {
        var delay = CalculateDelayBeforeDeadline(
            Stopwatch.GetTimestamp(),
            deadlineTimestamp,
            DefaultSpinThreshold,
            DefaultMaxPollDelay);

        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, cancellationToken);
            return;
        }

        while (Stopwatch.GetTimestamp() < deadlineTimestamp)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Thread.SpinWait(64);
        }
    }
}
