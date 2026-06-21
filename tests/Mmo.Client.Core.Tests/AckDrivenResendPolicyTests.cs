using Mmo.Client.Core;
using Xunit;

namespace Mmo.Client.Core.Tests;

// NET5b — headless coverage of the SHIPPED ack-driven re-send decision.
//
// Before NET5b, the only headless tests (TailLossResendHarnessTests) drove a hand-rolled ResendPolicy that
// RE-IMPLEMENTED the rule; MmoClient.DriveAckDrivenResend (the shipped wiring) was never exercised by any test, so a
// regression in it would only surface in a live run. NET5b extracts the decision into ONE pure helper
// (AckDrivenResendPolicy.Decide) that BOTH DriveAckDrivenResend AND these tests call. Asserting the helper here
// therefore asserts the exact rule the production wrapper executes — there is no longer a second copy to drift.
//
// These tests cover the four behaviours the wrapper relies on:
//   * clean play (ack keeps up, lead drains) -> NO re-send,
//   * overdue ack (lead > 0, conf stalled past the grace) -> re-send, bounded to at most one per cadence,
//   * K re-sends with conf stuck >= T -> ForceResync (the bounded fallback) exactly once, then counters reset,
//   * any conf advance resets the stall clock + the K counter (so the fallback never trips under healthy acks).
public sealed class AckDrivenResendPolicyTests
{
    // The shipped constants (mirrors MmoClient.ResendStallGraceMs / ResendFallbackCount / ResendFallbackStuckMs).
    private const double StallGraceMs = 350d;
    private const int FallbackCount = 6;
    private const double FallbackStuckMs = 1500d;
    private const double CadenceMs = 150d; // representative step cadence (the wrapper passes predictor.CadenceMs)

    private static AckResendConfig Config()
        => new(StallGraceMs, FallbackCount, FallbackStuckMs, CadenceMs);

    // ---- Clean play: conf keeps up so lead is 0 every poll -> the re-send never fires. -----------------------
    [Fact]
    public void CleanPlay_AckKeepsUp_NeverSends()
    {
        var config = Config();
        var state = default(AckResendState);
        var sends = 0;
        var resyncs = 0;

        // Walk 60 polls (~1 s at the 17 ms client poll). conf tracks pred exactly (lead always 0).
        for (var i = 0; i < 60; i++)
        {
            var now = i * 17d;
            uint pred = (uint)i;
            uint conf = (uint)i; // ack keeps up
            var decision = AckDrivenResendPolicy.Decide(now, pred, conf, emittedFreshThisPoll: false, state, config);
            state = decision.State;
            if (decision.SendBatch) sends++;
            if (decision.ForceResync) resyncs++;
        }

        Assert.Equal(0, sends);
        Assert.Equal(0, resyncs);
    }

    // ---- Overdue ack: lead > 0 and conf stalled -> re-send fires, but at MOST once per cadence. ---------------
    [Fact]
    public void OverdueAck_Resends_AtMostOncePerCadence()
    {
        var config = Config();
        var state = default(AckResendState);

        // pred=5, conf stuck at 3 (a stranded tail of 2). Poll every ~17 ms for 2 s; conf NEVER advances, but we
        // stop short of the fallback window by checking the cadence bound first.
        uint pred = 5;
        uint conf = 3;
        var sendTimes = new System.Collections.Generic.List<double>();

        for (var i = 0; i < 30; i++) // ~510 ms — past the 350 ms grace, but only ~3 cadence windows
        {
            var now = i * 17d;
            var decision = AckDrivenResendPolicy.Decide(now, pred, conf, emittedFreshThisPoll: false, state, config);
            state = decision.State;
            if (decision.SendBatch) sendTimes.Add(now);
        }

        // At least one re-send fired once the ack went overdue.
        Assert.True(sendTimes.Count > 0, "an overdue stalled ack should trigger at least one re-send");

        // The first re-send only fires after the stall grace elapsed (conf stalled since t=0).
        Assert.True(sendTimes[0] >= StallGraceMs, $"first re-send at {sendTimes[0]} ms must be >= grace {StallGraceMs}");

        // Cadence bound: consecutive re-sends are spaced at least CadenceMs apart (never two in one window).
        for (var i = 1; i < sendTimes.Count; i++)
        {
            Assert.True(sendTimes[i] - sendTimes[i - 1] >= CadenceMs,
                $"re-sends {sendTimes[i - 1]} -> {sendTimes[i]} closer than cadence {CadenceMs}");
        }
    }

    // ---- A fresh batch this poll covers the cadence: the re-send must NOT pile a second packet on top. --------
    [Fact]
    public void FreshEmitThisPoll_SuppressesResend_EvenWhenOverdue()
    {
        var config = Config();
        var state = new AckResendState
        {
            // conf stalled long ago (overdue) and we have a lead — the re-send WOULD fire if not for the fresh emit.
            HasLastConf = true,
            LastConf = 3,
            ConfStalledSinceMs = 0d,
            HasLastSentAt = false,
        };

        // now well past the grace, lead > 0, but a fresh batch already went out this poll.
        var decision = AckDrivenResendPolicy.Decide(
            nowMs: 1000d, pred: 5, conf: 3, emittedFreshThisPoll: true, state, config);

        Assert.False(decision.SendBatch);
        Assert.False(decision.ForceResync);
    }

    // ---- Black uplink: K re-sends with conf STILL stuck >= T -> ForceResync fires exactly once, then resets. ---
    [Fact]
    public void BlackUplink_AfterKResendsAndTms_ForceResyncsOnce_ThenResets()
    {
        var config = Config();
        var state = default(AckResendState);

        // conf is forever stuck at 3 while pred leads at 5 — the commit is undeliverable. Pump enough polls (well
        // past K cadence windows AND the T=1500 ms stuck threshold) and count the fallbacks.
        uint pred = 5;
        uint conf = 3;
        var sends = 0;
        var resyncs = 0;
        var resyncAtSends = new System.Collections.Generic.List<int>();

        // 4 s of polls at ~17 ms — plenty for K=6 re-sends spaced >= 150 ms (>= 900 ms) AND >= 1500 ms stuck.
        for (var i = 0; i < 240; i++)
        {
            var now = i * 17d;
            var decision = AckDrivenResendPolicy.Decide(now, pred, conf, emittedFreshThisPoll: false, state, config);
            state = decision.State;
            if (decision.SendBatch) sends++;
            if (decision.ForceResync)
            {
                resyncs++;
                resyncAtSends.Add(sends);
            }
        }

        // The fallback fired (re-send couldn't land), and not in a tight loop — at most one per ~K re-sends.
        Assert.True(resyncs >= 1, "a black uplink should trip the bounded ForceResync fallback");
        // The first resync only fired after at least K re-sends accumulated since the last conf advance.
        Assert.True(resyncAtSends[0] >= FallbackCount,
            $"first ForceResync after {resyncAtSends[0]} re-sends, expected >= K={FallbackCount}");
        // After a resync, the counter resets — so two resyncs are at least K re-sends apart (no tight loop).
        for (var i = 1; i < resyncAtSends.Count; i++)
        {
            Assert.True(resyncAtSends[i] - resyncAtSends[i - 1] >= FallbackCount,
                $"resyncs {resyncAtSends[i - 1]} -> {resyncAtSends[i]} re-sends apart, expected >= K={FallbackCount}");
        }
    }

    // ---- Conf advance resets the stall clock + the K counter: a healthy ack drip never reaches the fallback. ---
    [Fact]
    public void ConfAdvance_ResetsStallClockAndCounter_NoFallback()
    {
        var config = Config();
        var state = default(AckResendState);

        uint pred = 50;
        uint conf = 0;
        var resyncs = 0;
        var sends = 0;

        // Lead persists (pred always ahead) but conf advances by 1 every ~500 ms. 500 ms > the 350 ms grace, so the
        // ack goes overdue and re-sends DO fire in each gap — yet each advance resets the K counter (and the T-stuck
        // clock) long before K=6 consecutive re-sends accumulate, so the fallback never trips.
        for (var i = 0; i < 360; i++) // ~6 s
        {
            var now = i * 17d;
            // advance conf one step every ~500 ms, kept below pred so lead stays > 0 the whole run.
            var advanced = (uint)(now / 500d);
            conf = advanced < pred ? advanced : pred - 1;
            var decision = AckDrivenResendPolicy.Decide(now, pred, conf, emittedFreshThisPoll: false, state, config);
            state = decision.State;
            if (decision.SendBatch) sends++;
            if (decision.ForceResync) resyncs++;
        }

        // The re-send DID fire (the ack was overdue in the gaps between conf bumps)...
        Assert.True(sends > 0, "an overdue ack between conf advances should still re-send");
        // ...but the bounded ForceResync NEVER trips, because every conf advance reset the K counter and the T-stuck
        // clock before either fallback condition (K re-sends AND T ms stuck) could be met.
        Assert.Equal(0, resyncs);
    }
}
