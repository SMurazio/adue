using Mmo.Client.Core;
using Mmo.Server.Runtime;
using Mmo.Shared.Domain;
using Mmo.Shared.Protocol;
using Xunit;

namespace Mmo.Client.Core.Tests;

// NET5 — TAIL-LOSS / ACK-DRIVEN RE-SEND HEADLESS HARNESS.
//
// The bug NET5 fixes is the TAIL strand: the redundant StepCommitBatch window (8-deep) rides SUBSEQUENT packets,
// so a mid-stream single loss recovers within ~1 packet — but the LAST commit of a movement burst has no
// following packet to carry its redundant copy. If that final batch drops AND input has stopped, no further
// batch is ever sent, so the server's accepted step-seq (`conf`) stays permanently one (or N) behind the
// predictor's (`pred`) — a permanent `lead = pred - conf` that never drains. The existing
// TimingFaithfulReconcileHarness's UO-commit tests hide this because RunUoCommitStream force-DRAINS every
// in-flight batch at run-end (an artificial deliver-everything step). This rig deliberately does NOT drain: a
// dropped tail batch is gone unless a later packet re-carries it.
//
// The FIX (NET5 part 1) is ack-driven re-send: while `lead > 0` the client keeps shipping the current commit
// ring (the same redundant SendStepCommitBatch) at ~1 batch / cadence, INCLUDING after movement stops, until the
// server's ack catches the prediction. The server dedups (cursor) and applies at the authored tick, so a
// re-delivered tail commit just lands and `lead` drains with NO snap. This rig models that client re-send policy
// exactly as MmoClient.Poll implements it (drive the ring on a cadence timer whenever lead > 0), so we can:
//   (1) reproduce the stuck lead with re-send OFF (the bug), and
//   (2) prove the lead drains to 0 with re-send ON at a >=3%-equivalent tail drop (the fix), with NO snap.
//
// The FALLBACK (NET5 part 2) is the bounded ForceResync: at a heavier drop where re-send can't land (the link is
// black), after K re-sends with `conf` still stuck for T ms, call ForceResync to converge. The rig models that
// trigger too (re-sent K times, ack never advanced) and asserts convergence.
public sealed class TailLossResendHarnessTests
{
    private const int TickRate = 20;
    private const double TickMs = 1000d / TickRate;       // 50 ms/tick
    private const uint StepCooldownTicks = 3;             // 150 ms cadence
    private const double FrameMs = 1000d / 144d;          // ~144 Hz client poll
    private const uint AuthoredTickPastWindow = 64;
    private const uint AuthoredTickFutureLead = 4;
    private static double CadenceMs => MovementCadence.EffectiveStepCadenceMs(150, TickRate);

    // The ack-driven re-send policy NET5 adds to MmoClient.Poll, modelled headlessly. Mirrors the production
    // policy:
    //   * re-send fires only when lead > 0 AND the ack (conf) has been STALLED past a grace (>= ~one RTT + one
    //     cadence). In clean play conf advances every RTT, so the grace never elapses and NO extra packet is sent
    //     — the re-send is "the ack is OVERDUE", not "there is anything in flight". Under tail loss the conf
    //     stalls, the grace elapses, and we re-ship one batch per cadence until conf catches pred.
    //   * after K re-sends with conf STILL not advancing (>= T ms stalled), ForceResync (RESYNC1) converges.
    // RTT here is 2 x one-way latency (100 ms) = 200 ms.
    private sealed class ResendPolicy
    {
        public bool ResendEnabled;
        public bool FallbackEnabled;
        public double ResendIntervalMs = TailLossResendHarnessTests.CadenceMs; // ~1 batch / cadence (150 ms)
        public double StallGraceMs = 350d;                // ack overdue: > RTT(200) + one cadence(150)
        public int FallbackResendK = 6;                   // K re-sends with no ack progress => resync
        public double FallbackStuckMs = 1500d;            // T: conf stuck this long => resync
    }

    private sealed record RunResult(
        TileCoord ServerTile, uint ServerStepSeq,
        TileCoord PredictedTile, uint PredictedStepSeq,
        uint FinalConf, uint FinalLead,
        int Snapped, int Corrected, int ForceResyncs,
        int Resends);

    // Drives the UoClientDriven commit path WITHOUT the artificial run-end drain. Holds `held` until holdUntilMs
    // then releases. `dropBatchOnFrame` drops a batch on the uplink (the tail-loss injector). When the policy
    // enables re-send, the client re-ships the current ring on the cadence timer whenever lead > 0 (the NET5 fix).
    private static RunResult RunTailLoss(
        TileCoord start, Direction8 held, double runMs, double holdUntilMs,
        Func<int, bool> dropBatchOnFrame, ResendPolicy policy)
    {
        var grid = new TileGrid(512, 512, Array.Empty<TileCoord>());
        var server = new WorldEntity(1, 1, EntityKind.Player, start, held, "Local",
            Guid.NewGuid(), ownerSession: null, isDurable: true);
        var predictor = new LocalPlayerPredictor(start, held, CadenceMs, t => grid.IsWalkable(t), TickMs);
        predictor.SetClientDriven(true);
        predictor.SetIntent(true, held, TimeSpan.Zero);
        var released = false;

        const double latencyMs = 100d;
        const int ringCap = 8;
        var ring = new List<(uint Seq, uint Tick, Direction8 Dir)>();
        uint moveSeq = 0;
        uint serverCursor = 0;

        // The conf (RecipientStepSeq) the client has LEARNED from delivered snapshots — drives lead + the
        // reconcile re-base, exactly as MmoClient.ApplySnapshot feeds Reconcile(tile, recipientStepSeq).
        uint learnedConf = 0;
        var pendingBatches = new List<(double DeliverAtMs, StepCommitBatchMessage Batch)>();
        // Snapshots produced by the server, delivered downlink after latency (carry tile + StepSequence as conf).
        var pendingSnaps = new List<(double DeliverAtMs, uint ServerTick, TileCoord Tile, uint StepSeq)>();
        var acceptedBuffer = new Direction8[8];
        var acceptedTickBuffer = new long[8];
        uint nextServerTick = 0;

        // Re-send / fallback bookkeeping (the NET5 client state).
        var lastResendMs = double.NegativeInfinity;
        var resendsSinceConfAdvance = 0;
        var confStuckSinceMs = 0d;
        var lastConfForStuck = 0u;
        var resendCount = 0;
        var forceResyncs = 0;
        var snapped = 0;
        var corrected = 0;

        void ShipBatch(double nowMs)
        {
            if (ring.Count == 0)
            {
                return;
            }

            var head = ring[^1];
            var window = new List<StepCommitWindowEntry>();
            for (var i = ring.Count - 2; i >= 0; i--)
            {
                var delta = head.Seq - ring[i].Seq;
                if (delta is > 0 and <= byte.MaxValue && ring[i].Tick < head.Tick)
                {
                    window.Add(new StepCommitWindowEntry((byte)delta, head.Tick - ring[i].Tick, ring[i].Dir));
                }
            }

            pendingBatches.Add((nowMs + latencyMs, new StepCommitBatchMessage(head.Seq, head.Tick, head.Dir, window)));
        }

        for (var frame = 0; ; frame++)
        {
            var nowMs = frame * FrameMs;
            if (nowMs > runMs)
            {
                break;
            }

            if (!released && nowMs >= holdUntilMs)
            {
                predictor.SetIntent(false, held, TimeSpan.FromMilliseconds(nowMs));
                released = true;
            }

            // --- server: apply arrived fresh commits at their authored tick; emit a snapshot per tick ---
            while (nextServerTick * TickMs <= nowMs)
            {
                var tick = nextServerTick;
                var serverWallMs = tick * TickMs;

                var arrived = new SortedDictionary<uint, (uint AuthoredTick, Direction8 Dir)>();
                foreach (var (deliverAt, batch) in pendingBatches)
                {
                    if (deliverAt > serverWallMs)
                    {
                        continue;
                    }

                    foreach (var (seq, authoredTick, dir) in GameServer.ExtractFreshStepCommits(batch, serverCursor))
                    {
                        arrived[seq] = (authoredTick, dir);
                    }
                }

                foreach (var (seq, info) in arrived)
                {
                    if (server.TryCommitStepAuthored(
                            info.Dir, info.AuthoredTick, tick, StepCooldownTicks,
                            AuthoredTickPastWindow, AuthoredTickFutureLead, grid, out _))
                    {
                        serverCursor = seq;
                    }
                }

                // Emit the per-tick snapshot (downlink) carrying the server's current tile + accepted step-seq.
                pendingSnaps.Add((serverWallMs + latencyMs, tick, server.Tile, server.StepSequence));
                nextServerTick++;
            }

            // --- downlink: deliver snapshots whose latency elapsed; reconcile + learn conf ---
            pendingSnaps.Sort(static (a, b) => a.DeliverAtMs.CompareTo(b.DeliverAtMs));
            var deliveredSnaps = 0;
            foreach (var (deliverAt, sTick, tile, stepSeq) in pendingSnaps)
            {
                if (deliverAt > nowMs)
                {
                    break;
                }

                deliveredSnaps++;
                var receivedAt = TimeSpan.FromMilliseconds(nowMs);
                predictor.CalibrateToServerTick(sTick, receivedAt);
                var outcome = predictor.Reconcile(tile, stepSeq, receivedAt);
                if (outcome == LocalPlayerPredictor.ReconcileOutcome.Snapped) snapped++;
                if (outcome == LocalPlayerPredictor.ReconcileOutcome.Corrected) corrected++;
                learnedConf = stepSeq;
            }

            if (deliveredSnaps > 0)
            {
                pendingSnaps.RemoveRange(0, deliveredSnaps);
            }

            // --- client: tick the predictor; bank + ship newly accepted steps ---
            predictor.Tick(TimeSpan.FromMilliseconds(nowMs), acceptedBuffer, acceptedTickBuffer, out var acceptedCount);
            var emit = Math.Min(acceptedCount, acceptedBuffer.Length);
            for (var i = 0; i < emit; i++)
            {
                ring.Add((++moveSeq, (uint)Math.Max(0, acceptedTickBuffer[i]), acceptedBuffer[i]));
                if (ring.Count > ringCap)
                {
                    ring.RemoveAt(0);
                }
            }

            var droppedThisBatch = dropBatchOnFrame(frame);
            if (emit > 0 && !droppedThisBatch)
            {
                ShipBatch(nowMs);
                lastResendMs = nowMs;
            }

            // --- NET5 ack-driven re-send: while lead > 0 AND the ack is OVERDUE (conf stalled past the grace), ---
            // re-ship the ring on the cadence timer (incl. after stop) until conf catches pred.
            var pred = predictor.PredictedStepSeq;
            var lead = pred > learnedConf ? pred - learnedConf : 0u;

            // Track when conf last advanced (resets the stall clock + fallback counters on any ack progress).
            if (learnedConf != lastConfForStuck)
            {
                lastConfForStuck = learnedConf;
                confStuckSinceMs = nowMs;
                resendsSinceConfAdvance = 0;
            }

            var ackOverdue = nowMs - confStuckSinceMs >= policy.StallGraceMs;
            if (policy.ResendEnabled && lead > 0 && emit == 0 && ackOverdue
                && nowMs - lastResendMs >= policy.ResendIntervalMs)
            {
                // Re-ship with NO new step — the tail-recovery packet — but only when we did NOT just drop (model
                // the drop on the re-send packet too, so a black uplink genuinely loses it).
                if (!droppedThisBatch)
                {
                    ShipBatch(nowMs);
                    resendCount++;
                }

                resendsSinceConfAdvance++;
                lastResendMs = nowMs;

                // Bounded ForceResync fallback: re-sent K times AND conf stuck >= T ms => the commit is genuinely
                // undeliverable; converge via the RESYNC1 primitive.
                if (policy.FallbackEnabled
                    && resendsSinceConfAdvance >= policy.FallbackResendK
                    && nowMs - confStuckSinceMs >= policy.FallbackStuckMs)
                {
                    predictor.ForceResync();
                    forceResyncs++;
                    resendsSinceConfAdvance = 0;
                    confStuckSinceMs = nowMs;
                }
            }
        }

        var finalPred = predictor.PredictedStepSeq;
        var finalLead = finalPred > learnedConf ? finalPred - learnedConf : 0u;
        return new RunResult(
            server.Tile, server.StepSequence, predictor.PredictedTile, finalPred,
            learnedConf, finalLead, snapped, corrected, forceResyncs, resendCount);
    }

    // ---- STEP 1 (MANDATE): reproduce the stuck lead on the CURRENT behaviour (re-send OFF) -----------------
    //
    // Hold east a short burst, then drop the TAIL batch (the last frame that produced a commit) and STOP input.
    // With no re-send, that final commit is never re-delivered: the server's accepted step-seq stays permanently
    // behind the prediction. The lead does NOT drain to 0 — the permanent strand.
    [Fact]
    public void Step1_TailLossWithNoResend_LeavesLeadPermanentlyStuck()
    {
        // Drop the LAST ~3 batches of the movement burst (the tail), then stop input at 900 ms. After ~600 ms of
        // walking (~4 steps) the burst ends; dropping its tail batches strands the final commit(s).
        var result = RunTailLoss(
            start: new TileCoord(200, 200), held: Direction8.E, runMs: 4000d, holdUntilMs: 900d,
            dropBatchOnFrame: f => f * FrameMs >= 700d && f * FrameMs < 900d,
            policy: new ResendPolicy { ResendEnabled = false, FallbackEnabled = false });

        // THE BUG: with no re-send, the server never receives the dropped tail commit, so conf < pred forever.
        Assert.True(result.FinalLead > 0,
            $"expected a permanently stuck lead with no re-send, but lead drained to {result.FinalLead} " +
            $"(pred={result.PredictedStepSeq}, conf={result.FinalConf})");
        Assert.True(result.ServerStepSeq < result.PredictedStepSeq,
            $"server step-seq {result.ServerStepSeq} should be stuck behind prediction {result.PredictedStepSeq}");
        // No re-sends fired (the policy was off) — confirming the gap is the missing re-send, not something else.
        Assert.Equal(0, result.Resends);
    }

    // ---- STEP 2 (FIX): the same tail loss DRAINS to 0 with ack-driven re-send, NO snap ----------------------
    [Fact]
    public void Step2_TailLossWithResend_LeadDrainsToZero_NoSnap()
    {
        var result = RunTailLoss(
            start: new TileCoord(200, 200), held: Direction8.E, runMs: 4000d, holdUntilMs: 900d,
            dropBatchOnFrame: f => f * FrameMs >= 700d && f * FrameMs < 900d,
            policy: new ResendPolicy { ResendEnabled = true, FallbackEnabled = true });

        // THE FIX: the re-send re-ships the tail commit on the next cadence; the server applies it at its authored
        // tick (dedup) and conf catches pred — lead drains to 0.
        Assert.Equal(0u, result.FinalLead);
        Assert.Equal(result.PredictedStepSeq, result.ServerStepSeq);
        Assert.Equal(result.PredictedTile, result.ServerTile);
        // Seamless: the recovery was a benign late confirm, never a forced snap (the prediction was right).
        Assert.Equal(0, result.Snapped);
        // The fallback never needed to fire — the re-send healed it first (a >=3%-equivalent tail drop is tier 1).
        Assert.Equal(0, result.ForceResyncs);
        // At least one tail-recovery re-send actually fired (the heal was via re-send, not luck).
        Assert.True(result.Resends > 0, "expected at least one ack-driven re-send to recover the tail");
    }

    // ---- STEP 3 (FALLBACK): a black uplink (re-send can't land) converges via the bounded ForceResync --------
    [Fact]
    public void Step3_UndeliverableTail_FallsBackToForceResync_AndConverges()
    {
        // The uplink goes BLACK from 700 ms onward: every batch (fresh OR re-send) is dropped for the rest of the
        // run, so the tail commit is genuinely undeliverable. Re-send tries K times, conf never advances past the
        // last good commit, and the bounded fallback fires ForceResync to converge the prediction onto the server.
        var result = RunTailLoss(
            start: new TileCoord(200, 200), held: Direction8.E, runMs: 6000d, holdUntilMs: 900d,
            dropBatchOnFrame: f => f * FrameMs >= 700d,
            policy: new ResendPolicy { ResendEnabled = true, FallbackEnabled = true });

        // The fallback fired (re-send couldn't land — heavy/black loss is tier 2).
        Assert.True(result.ForceResyncs > 0, "expected the bounded ForceResync fallback to fire on a black uplink");
        // After ForceResync converges the prediction onto the last confirmed server state, pred == conf (no
        // permanent strand): the resync re-anchored pred down to conf.
        Assert.Equal(0u, result.FinalLead);
        Assert.Equal(result.FinalConf, result.PredictedStepSeq);
    }

    // ---- REGRESSION GUARD: clean play (no loss) sends NO extra re-send packets and never force-snaps ---------
    [Fact]
    public void Regression_CleanPlay_NoExtraResends_NoSnap_NoFallback()
    {
        var result = RunTailLoss(
            start: new TileCoord(200, 200), held: Direction8.E, runMs: 4000d, holdUntilMs: 900d,
            dropBatchOnFrame: _ => false,
            policy: new ResendPolicy { ResendEnabled = true, FallbackEnabled = true });

        // No loss => every fresh commit is acked within the RTT; lead drains via normal snapshots, so the
        // ack-driven re-send NEVER trips (re-send only fires while lead > 0 AND the cadence timer elapses without a
        // fresh batch — which never happens in clean play because conf keeps up). Zero extra packets, zero snaps,
        // zero fallback.
        Assert.Equal(0, result.Resends);
        Assert.Equal(0, result.Snapped);
        Assert.Equal(0, result.ForceResyncs);
        Assert.Equal(0u, result.FinalLead);
        Assert.Equal(result.PredictedStepSeq, result.ServerStepSeq);
    }
}
