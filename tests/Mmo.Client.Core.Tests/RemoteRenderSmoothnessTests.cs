using Mmo.Client.Core;
using Mmo.Client.Core.Continuous;
using Mmo.Shared.Domain;
using Xunit;

namespace Mmo.Client.Core.Tests;

// REMOTE-RENDER SMOOTHNESS HARNESS (todo/S-remote-render-jitter-200-clients + N-remote-smoothness-tooling #1).
// Reproduces HEADLESSLY the crowd shimmer measured live via client_render_trace (2026-07-02, 200-bot stress):
// at the DEFAULT delay-0 extrapolate-to-now setting, every arriving snapshot re-bases the extrapolation and the
// discontinuity (Q12.4 quantization + heading change + arrival jitter) was absorbed IN ONE FRAME — a small
// backward step, i.e. a >90 deg per-frame velocity flip. Live numbers: 11% of frames reversed at 120 bots
// (regular arrivals), 26% at 200 (bursty arrivals from the near-saturated tick), maxJerk 30 u/s/frame.
//
// The harness drives the REAL RemotePositionInterpolator with synthetic 20Hz sample streams (positions quantized
// exactly like the wire's Q12.4, velocities like the 1/256 wire scale) and samples it at 60fps, computing the SAME
// metrics client_render_trace reports (speedStdDev / maxJerk / reversals / renderVsTruth). PRE-FIX MEASUREMENT
// (this harness, before the correction smoothing): bursty arrivals = 37 reversals / 3.8s — the headless twin of
// the live 26%; quantized-regular and turning alone produced 0 reversals headless (at walk speed the per-sample
// re-base error stays under one frame of motion), so the live 11% baseline is arrival-timing noise too — the
// bursty scenario IS the mechanism. The correction-smoothing fix must hold the bounds below WITHOUT adding
// latency (the latency guards pin render-vs-truth and stop-settle, so smoothing can never become a laggy tween).
public sealed class RemoteRenderSmoothnessTests
{
    private const double TickIntervalMs = 50d;      // 20Hz server
    private const double FrameIntervalMs = 1000d / 60d;
    private const double WalkSpeed = 4d;            // units/sec — the live walk speed the bots used
    private const double SpeedEpsilon = 0.05;       // ignore ~standstill frames in the reversal count — SAME value + shape as the live trace (MmoClientRoot render_trace: dot < 0 && current speed > 0.05)

    private static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);

    // Q12.4 — the exact wire quantization (1/16 u), applied to every sampled position like PositionEncoding does.
    private static double Q16(double v) => Math.Round(v * 16d, MidpointRounding.AwayFromZero) / 16d;

    // 1/256 u/s — the wire's velocity scale.
    private static double Q256(double v) => Math.Round(v * 256d, MidpointRounding.AwayFromZero) / 256d;

    private sealed record Metrics(
        int Frames,
        int Reversals,
        double MeanSpeed,
        double SpeedStdDev,
        double MaxJerk,
        double MeanRenderVsTruth,
        double MaxRenderVsTruth);

    // Drive the interpolator: server truth is (pos(t), vel(t)); one sample per 50ms tick, quantized, arriving at
    // tick-time + latencyOf(tick); the client Samples at 60fps over [warmupMs, endMs]. Confirm() calls are
    // interleaved with Sample() calls in true arrival order — exactly like the live client's poll loop.
    private static Metrics RunScenario(
        Func<double, (double X, double Y)> truthPos,
        Func<double, (double X, double Y)> truthVel,
        Func<int, double> latencyOf,
        double durationMs,
        bool quantize = true,
        double warmupMs = 200d)
    {
        var start = truthPos(0);
        var interp = new RemotePositionInterpolator(new WorldVector(start.X, start.Y), interpolationDelayMs: 0d);

        // Generate the arrival-ordered sample stream.
        var arrivals = new List<(double ArrivalMs, WorldVector Pos, WorldVector Vel)>();
        for (var tick = 0; tick * TickIntervalMs <= durationMs; tick++)
        {
            var t = tick * TickIntervalMs;
            var p = truthPos(t / 1000d);
            var v = truthVel(t / 1000d);
            var pos = quantize ? new WorldVector(Q16(p.X), Q16(p.Y)) : new WorldVector(p.X, p.Y);
            var vel = quantize ? new WorldVector(Q256(v.X), Q256(v.Y)) : new WorldVector(v.X, v.Y);
            arrivals.Add((t + latencyOf(tick), pos, vel));
        }

        arrivals.Sort((a, b) => a.ArrivalMs.CompareTo(b.ArrivalMs));

        // Interleave: per 60fps frame, first deliver every sample that arrived before this frame, then Sample.
        var renders = new List<(double NowMs, RenderPosition Render)>();
        var next = 0;
        for (var nowMs = warmupMs; nowMs <= durationMs; nowMs += FrameIntervalMs)
        {
            while (next < arrivals.Count && arrivals[next].ArrivalMs <= nowMs)
            {
                interp.Confirm(arrivals[next].Pos, Ms(arrivals[next].ArrivalMs), 0d, arrivals[next].Vel);
                next++;
            }

            renders.Add((nowMs, interp.Sample(Ms(nowMs))));
        }

        // Metrics — the same definitions client_render_trace uses.
        var speeds = new List<double>();
        var reversals = 0;
        var maxJerk = 0d;
        (double X, double Y)? prevVel = null;
        for (var i = 1; i < renders.Count; i++)
        {
            var dt = (renders[i].NowMs - renders[i - 1].NowMs) / 1000d;
            var vx = (renders[i].Render.X - renders[i - 1].Render.X) / dt;
            var vy = (renders[i].Render.Y - renders[i - 1].Render.Y) / dt;
            var speed = Math.Sqrt((vx * vx) + (vy * vy));
            speeds.Add(speed);
            if (prevVel is { } pv)
            {
                var jerk = Math.Sqrt(Math.Pow(vx - pv.X, 2) + Math.Pow(vy - pv.Y, 2));
                maxJerk = Math.Max(maxJerk, jerk);
                // Reversal definition mirrors the live client_render_trace exactly: a >90° flip (dot < 0) while the
                // CURRENT frame speed is above the trace's epsilon — so a headless bound here is comparable 1:1
                // with a live trace read.
                if (((vx * pv.X) + (vy * pv.Y)) < 0d && speed > SpeedEpsilon)
                {
                    reversals++;
                }
            }

            prevVel = (vx, vy);
        }

        var meanSpeed = speeds.Average();
        var stdDev = Math.Sqrt(speeds.Average(s => Math.Pow(s - meanSpeed, 2)));

        var truthErrors = renders.Select(r =>
        {
            var p = truthPos(r.NowMs / 1000d);
            return Math.Sqrt(Math.Pow(r.Render.X - p.X, 2) + Math.Pow(r.Render.Y - p.Y, 2));
        }).ToList();

        return new Metrics(renders.Count, reversals, meanSpeed, stdDev, maxJerk, truthErrors.Average(), truthErrors.Max());
    }

    private static Func<int, double> RegularLatency => _ => 5d;

    // The 200-client server signature: the tick runs late every few ticks, so one sample arrives ~35ms late and
    // the NEXT lands nearly on top of it (a bunched pair). Deterministic (no RNG — repeatable numbers).
    private static Func<int, double> BurstyLatency => tick => tick % 4 == 3 ? 40d : 5d;

    // ---- Scenario 1: clean baseline — constant velocity, regular arrivals, NO quantization. ----------------
    // Extrapolate-to-now is exactly continuous here (each new sample lands precisely where the extrapolation
    // already was), so this must be perfectly smooth BOTH pre- and post-fix. Pins the harness itself.
    [Fact]
    public void ConstantVelocity_RegularArrivals_Unquantized_IsPerfectlySmooth()
    {
        var m = RunScenario(
            t => (WalkSpeed * t, 0d),
            _ => (WalkSpeed, 0d),
            RegularLatency,
            durationMs: 4000d,
            quantize: false);

        Assert.Equal(0, m.Reversals);
        Assert.True(m.SpeedStdDev < 0.05, $"speedStdDev {m.SpeedStdDev:0.###}");
        Assert.True(m.MaxJerk < 0.5, $"maxJerk {m.MaxJerk:0.###}");
    }

    // ---- Scenario 2: constant velocity + the REAL wire quantization, regular arrivals. ----------------------
    // Quantization alone stays JUST under one frame of motion at walk speed (1/16u vs 0.067u/frame), so this
    // passes even pre-fix — kept as the regression floor (a faster entity or coarser encoding would flip it, and
    // the smoothing must never make the clean case worse).
    [Fact]
    public void ConstantVelocity_Quantized_RegularArrivals_NoReversals()
    {
        var m = RunScenario(
            t => (WalkSpeed * t, 0d),
            _ => (WalkSpeed, 0d),
            RegularLatency,
            durationMs: 4000d);

        Assert.Equal(0, m.Reversals);
        Assert.True(m.SpeedStdDev < 1.0, $"speedStdDev {m.SpeedStdDev:0.###}");
        Assert.True(m.MaxJerk < 6.0, $"maxJerk {m.MaxJerk:0.###}");
    }

    // ---- Scenario 3: constant velocity + quantization + BURSTY arrivals (the 200-client tick signature). ----
    // PRE-FIX: 37 reversals / 3.8s in this harness (live: 26% of frames at 200 bots) — the extrapolation
    // overruns during the late gap, then the bunched pair re-bases it BACKWARD hard, every 4 ticks.
    // Post-fix: the re-base error is absorbed over ~100ms — a couple residual reversals at most, jerk tamed.
    [Fact]
    public void ConstantVelocity_Quantized_BurstyArrivals_ShimmerIsSmoothed()
    {
        var m = RunScenario(
            t => (WalkSpeed * t, 0d),
            _ => (WalkSpeed, 0d),
            BurstyLatency,
            durationMs: 4000d);

        Assert.True(m.Reversals <= 2, $"reversals {m.Reversals} (pre-fix: 37)");
        Assert.True(m.SpeedStdDev < 1.6, $"speedStdDev {m.SpeedStdDev:0.###}");
        Assert.True(m.MaxJerk < 10.0, $"maxJerk {m.MaxJerk:0.###}");
    }

    // ---- Scenario 4: TURNING entity (the gnoll-chase / waypoint-bot shape) + quantization, regular arrivals. -
    // The replicated velocity is tangent at the sample instant, so the extrapolation leaves the curve and every
    // new sample pulls it back. With REGULAR arrivals the pull-back stays under a frame of motion (passes
    // pre-fix, kept as the floor); combine it with arrival jitter (scenario 3) and it flips — the live gnoll
    // walk jitter is the two stacked. The smoothing must keep the curve clean.
    [Fact]
    public void TurningEntity_Quantized_RegularArrivals_NoPerpendicularReversals()
    {
        const double omega = Math.PI / 4d;          // 45 deg/s heading rotation
        const double radius = WalkSpeed / omega;    // speed stays 4 u/s on the circle
        var m = RunScenario(
            t => (radius * Math.Cos(omega * t), radius * Math.Sin(omega * t)),
            t => (-radius * omega * Math.Sin(omega * t), radius * omega * Math.Cos(omega * t)),
            RegularLatency,
            durationMs: 4000d);

        Assert.Equal(0, m.Reversals);
        Assert.True(m.MaxJerk < 8.0, $"maxJerk {m.MaxJerk:0.###}");
    }

    // ---- Latency guard 1: the smoothing must NOT become a laggy tween. -------------------------------------
    // Render-vs-truth for a steady walker stays within the extrapolate-to-now error budget (~latency x speed +
    // quantization + smoothing residual). A buffer/tween regression (e.g. an accidental 100ms delay = 0.4u at
    // walk speed) fails this loudly.
    [Fact]
    public void Smoothing_AddsNoMeaningfulLatency_OnASteadyWalker()
    {
        var m = RunScenario(
            t => (WalkSpeed * t, 0d),
            _ => (WalkSpeed, 0d),
            RegularLatency,
            durationMs: 4000d);

        Assert.True(m.MeanRenderVsTruth < 0.12, $"meanRenderVsTruth {m.MeanRenderVsTruth:0.###}u — smoothing is adding lag");
        Assert.True(m.MaxRenderVsTruth < 0.30, $"maxRenderVsTruth {m.MaxRenderVsTruth:0.###}u");
    }

    // ---- Latency guard 3 (review F2): a sharp 180° REVERSAL turns the render around promptly. ----------------
    // The user's most latency-sensitive case: an entity walking +X reverses to -X mid-sample-interval. The
    // decaying offset's drift can briefly outweigh the reversed base velocity, so the on-screen turnaround may
    // trail the reversal SAMPLE by a couple frames (reviewer-quantified worst ~2 frames at walk speed, hard-capped
    // ~85ms by MaxCorrectionUnits) — but never more. Pins that the smoothing can't soften direction changes into
    // visible input lag: within 4 frames (~67ms) of the reversal sample arriving, the render must move -X.
    [Fact]
    public void SharpReversal_TurnsAroundWithinFourFramesOfTheSample()
    {
        const double reverseAtSec = 2.025; // mid-interval (between the t=2.0s and t=2.05s ticks) — the worst case
        (double X, double Y) Pos(double t) => t < reverseAtSec
            ? (WalkSpeed * t, 0d)
            : ((WalkSpeed * reverseAtSec) - (WalkSpeed * (t - reverseAtSec)), 0d);
        (double X, double Y) Vel(double t) => (t < reverseAtSec ? WalkSpeed : -WalkSpeed, 0d);

        var start = Pos(0);
        var interp = new RemotePositionInterpolator(new WorldVector(start.X, start.Y), 0d);

        // First post-reversal sample is the t=2.05s tick; with the 5ms latency it ARRIVES at 2055ms.
        const double reversalSampleArrivesMs = 2055d;
        var arrivals = new List<(double ArrivalMs, WorldVector Pos, WorldVector Vel)>();
        for (var tick = 0; tick * TickIntervalMs <= 3000d; tick++)
        {
            var t = tick * TickIntervalMs / 1000d;
            var p = Pos(t);
            var v = Vel(t);
            arrivals.Add(((tick * TickIntervalMs) + 5d, new WorldVector(Q16(p.X), Q16(p.Y)), new WorldVector(Q256(v.X), Q256(v.Y))));
        }

        var next = 0;
        var previousX = double.NaN;
        var turnedAroundAtMs = double.NaN;
        for (var nowMs = 200d; nowMs <= 2400d; nowMs += FrameIntervalMs)
        {
            while (next < arrivals.Count && arrivals[next].ArrivalMs <= nowMs)
            {
                interp.Confirm(arrivals[next].Pos, Ms(arrivals[next].ArrivalMs), 0d, arrivals[next].Vel);
                next++;
            }

            var render = interp.Sample(Ms(nowMs));
            if (!double.IsNaN(previousX) && nowMs > reversalSampleArrivesMs && render.X < previousX - 1e-6 && double.IsNaN(turnedAroundAtMs))
            {
                turnedAroundAtMs = nowMs;
            }

            previousX = render.X;
        }

        Assert.False(double.IsNaN(turnedAroundAtMs), "render never turned around after the reversal");
        var framesLate = (turnedAroundAtMs - reversalSampleArrivesMs) / FrameIntervalMs;
        Assert.True(framesLate <= 4d, $"render turned around {framesLate:0.#} frames after the reversal sample (bound: 4)");
    }

    // ---- Latency guard 2: a sharp STOP settles fast (the correction decay must not coast). ------------------
    // Truth walks then stops dead at t=2s (the stop-edge sample replicates position + velocity 0). The render
    // must sit within 0.05u of the stop point by 250ms after the stop sample lands — a long coast/springy settle
    // means the smoothing constant is too soft (the responsiveness-over-wobble rule).
    [Fact]
    public void SharpStop_SettlesWithin250ms()
    {
        const double stopAtSec = 2d;
        var stopPos = WalkSpeed * stopAtSec;

        (double X, double Y) Pos(double t) => (Math.Min(t, stopAtSec) * WalkSpeed, 0d);
        (double X, double Y) Vel(double t) => (t < stopAtSec ? WalkSpeed : 0d, 0d);

        var start = Pos(0);
        var interp = new RemotePositionInterpolator(new WorldVector(start.X, start.Y), 0d);
        for (var tick = 0; tick * TickIntervalMs <= 3000d; tick++)
        {
            var t = tick * TickIntervalMs / 1000d;
            var p = Pos(t);
            var v = Vel(t);
            interp.Confirm(new WorldVector(Q16(p.X), Q16(p.Y)), Ms((tick * TickIntervalMs) + 5d), 0d, new WorldVector(Q256(v.X), Q256(v.Y)));

            // Sample a frame right after each arrival so the interpolator's clock advances realistically.
            interp.Sample(Ms((tick * TickIntervalMs) + 6d));
        }

        // 250ms after the stop sample (t=2000ms, arrives 2005ms): the render must have settled onto the stop.
        var settled = interp.Sample(Ms(2255d));
        Assert.True(Math.Abs(settled.X - stopPos) < 0.05, $"render {settled.X:0.###} vs stop {stopPos} — coasting past the stop");
    }
}
