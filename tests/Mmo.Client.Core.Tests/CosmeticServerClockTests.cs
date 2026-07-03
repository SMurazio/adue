using Mmo.Client.Core;
using Xunit;

namespace Mmo.Client.Core.Tests;

// TELEGRAPH T2 (docs/ability-telegraph-sync-design.md): the cosmetic server-clock estimator — the presentation-only
// "what server tick is it roughly now?" that drives the telegraph fill. Pinned headlessly in the smoothness-harness
// style (RemoteRenderSmoothnessTests): synthetic 20 Hz snapshot streams whose arrivals carry base latency + seeded
// random jitter, fed in arrival order, then the estimate is judged against the true server timeline. The estimator
// must (a) snap immediately on the first observation, (b) CONVERGE under jitter — settle near the mean-latency
// offset instead of chasing each burst, (c) advance smoothly between arrivals off the local clock, and (d) re-snap
// on a clock STEP (reconnect/pause) instead of averaging through it for minutes.
public sealed class CosmeticServerClockTests
{
    private const int TickRate = 20;
    private const double TickMs = 1000d / TickRate;

    private static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);

    [Fact]
    public void NoEstimateBeforeFirstObservation()
    {
        var clock = new CosmeticServerClock();

        Assert.False(clock.HasEstimate);
        Assert.Null(clock.EstimateServerTick(Ms(1000)));
    }

    [Fact]
    public void FirstObservationSnapsAndExtrapolatesAtTickRate()
    {
        var clock = new CosmeticServerClock();

        clock.ObserveSnapshot(1000, Ms(5000), TickRate);

        Assert.True(clock.HasEstimate);
        // At the observation instant the estimate IS the observed tick...
        Assert.Equal(1000d, clock.EstimateServerTick(Ms(5000))!.Value, 6);
        // ...and between arrivals it advances off the local clock at tick rate (500 ms = 10 ticks @ 20 Hz),
        // fractionally (the fill wants sub-tick smoothness at render rate).
        Assert.Equal(1010d, clock.EstimateServerTick(Ms(5500))!.Value, 6);
        Assert.Equal(1002.5d, clock.EstimateServerTick(Ms(5125))!.Value, 6);
    }

    // The core convergence property: 20 Hz snapshots arriving with 80 ms base latency + seeded 0..60 ms jitter. The
    // estimate must settle near the MEAN-latency offset (the estimator is systematically late by ~one-way latency —
    // inherent and fine, the resolve's observable effects ride the same wire) and, once warm, must be STABLE: the
    // per-arrival wobble must be far smaller than the raw jitter amplitude, or the fill would visibly stutter.
    [Fact]
    public void ConvergesUnderJitteredArrivals()
    {
        var clock = new CosmeticServerClock();
        var rng = new Random(4242);
        const double baseDelayMs = 80d;
        const double jitterMs = 60d;
        const uint firstTick = 10_000;

        // Shared timeline: server tick (firstTick + i) happens at local i*50 ms; its snapshot arrives delay_i later.
        // True server tick at local time L is therefore firstTick + L/50.
        var errors = new List<double>(); // estimate − trueTick, sampled at each post-warmup arrival instant
        for (var i = 0; i < 400; i++)
        {
            var delay = baseDelayMs + (rng.NextDouble() * jitterMs);
            var arrivalMs = (i * TickMs) + delay;
            clock.ObserveSnapshot(firstTick + (uint)i, Ms(arrivalMs), TickRate);

            if (i >= 100)
            {
                var trueTick = firstTick + (arrivalMs / TickMs);
                errors.Add(clock.EstimateServerTick(Ms(arrivalMs))!.Value - trueTick);
            }
        }

        // (b) settled near the mean-latency offset: mean delay = 80 + 30 = 110 ms = 2.2 ticks behind truth. Every
        // post-warmup error stays inside the physical delay band (80..140 ms → 1.6..2.8 ticks late) with margin.
        Assert.All(errors, error => Assert.InRange(error, -3.0d, -1.4d));
        var mean = errors.Average();
        Assert.InRange(mean, -2.6d, -1.8d);

        // (c) stability: the EMA must flatten the ±0.6-tick raw arrival jitter to a small per-arrival wobble. Max
        // absolute deviation from the settled mean stays under half a tick (raw samples alone would exceed it).
        Assert.All(errors, error => Assert.True(Math.Abs(error - mean) < 0.5d, $"estimate wobble {error - mean:0.###} ticks"));
    }

    // A clock STEP (long pause / reconnect: the local clock leapt but ticks kept flowing, so the offset jumped by
    // far more than jitter) must SNAP, not EMA-crawl: one post-step observation re-anchors the estimate.
    [Fact]
    public void SnapsOnClockStepInsteadOfCrawling()
    {
        var clock = new CosmeticServerClock();
        for (var i = 0; i < 50; i++)
        {
            clock.ObserveSnapshot(1000 + (uint)i, Ms((i * TickMs) + 100d), TickRate);
        }

        // 30 s stall: the next snapshot's tick is 600 ticks further on but arrives at a local time 40 s later —
        // the offset sample moves by ~200 ticks (10 s), way past the 2 s snap threshold.
        clock.ObserveSnapshot(1000 + 49 + 600, Ms((49 * TickMs) + 100d + 40_000d), TickRate);

        var estimate = clock.EstimateServerTick(Ms((49 * TickMs) + 100d + 40_000d))!.Value;
        Assert.Equal(1000d + 49d + 600d, estimate, 6);
    }

    // Defensive: a nonsense tick rate must not poison the running estimate.
    [Fact]
    public void IgnoresNonPositiveTickRate()
    {
        var clock = new CosmeticServerClock();
        clock.ObserveSnapshot(1000, Ms(0), TickRate);

        clock.ObserveSnapshot(2000, Ms(100), 0);
        clock.ObserveSnapshot(2000, Ms(100), -5);

        Assert.Equal(1002d, clock.EstimateServerTick(Ms(100))!.Value, 6);
    }
}
