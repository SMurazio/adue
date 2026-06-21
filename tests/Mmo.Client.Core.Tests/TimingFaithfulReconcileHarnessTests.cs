using Mmo.Client.Core;
using Mmo.Server.Runtime;
using Mmo.Shared.Domain;
using Xunit;

namespace Mmo.Client.Core.Tests;

// TEST1 — TIMING-FAITHFUL HEADLESS RECONCILE/FEEL HARNESS (the test UO5 should have had).
//
// Every other predictor test in this project drives an IDEALIZED tick timeline: it ticks the predictor and
// reconciles it against FRESH server state on the SAME tick, at zero latency, with a confirm available every
// step. Under that timeline `serverStepSeq` is whatever the test hands in, so the snapshot-vs-cadence mismatch
// that NORMAL walking creates is never exercised — and that mismatch is exactly what UO5 (`9cf3abf`, reverted
// `1bb3ad6`) misread: it shipped 237/237-green but felt "much worse" live.
//
// THE REAL TIMING this rig models (none of which the idealized tests do):
//   * SERVER steps on its 20 Hz tick grid (50 ms/tick) via the REAL WorldEntity.TryStep, but only every
//     stepCooldownTicks (3 ticks = 150 ms) does an accepted step actually advance Tile + StepSequence. So during
//     a sustained straight run StepSequence climbs only ~1 in every 3 ticks.
//   * SNAPSHOTS go out at 20 Hz (one per tick) carrying the server's (Tile, StepSequence as RecipientStepSeq,
//     serverTick). Because the server steps only every 3rd tick, the RecipientStepSeq the client sees is FLAT
//     for two snapshots out of three — the precise "serverStepSeq advances ~every 3rd snapshot, NOT every
//     snapshot" condition the task calls out.
//   * The CLIENT polls at a high frame rate (here ~144 Hz) and, per the real MmoClient loop, calls
//     predictor.Tick(now) every frame; on each DELIVERED snapshot it calls CalibrateToServerTick(serverTick,
//     receivedAt) then Reconcile(tile, recipientStepSeq, receivedAt) — exactly MmoClient.Poll / ApplySnapshot.
//   * Configurable LATENCY (one-way: a snapshot produced at server-wall T is delivered to the client at
//     T + latency), JITTER (±ms wobble on each delivery), and DROP (skip delivering a chosen snapshot — the
//     next one's cumulative RecipientStepSeq must re-sync).
//
// It records every ReconcileOutcome (Matched/Corrected/Snapped) AND the per-frame render position so the
// invariants can assert "zero corrections during steady walking" and "render glides monotonically forward".
//
// The rig is deliberately reusable: NewRig + RunStraightRun take the render mode (client-driven vs server-paced),
// direction, latency, jitter, drop set, and a walkability oracle (via the grid), and return a RunResult the tests
// assert against. The UO5 RE-ATTEMPT slots its "frame-drop overshoot converges back" test in against THIS rig (see
// the marker comment at the bottom) — that bug is UNFIXED on the current reverted code, so it is intentionally NOT
// asserted here.
public sealed class TimingFaithfulReconcileHarnessTests
{
    // ---- timing constants (mirror the real 20 Hz server tick + 150 ms step cadence) --------------------
    private const int TickRate = 20;                    // 20 Hz
    private const double TickMs = 1000d / TickRate;     // 50 ms/tick
    private const uint StepCooldownTicks = 3;           // 150 ms cadence => server steps every 3rd tick
    private const double FrameMs = 1000d / 144d;        // client renders/polls at ~144 Hz (frame << tick)

    private static double CadenceMs => MovementCadence.EffectiveStepCadenceMs(150, TickRate); // 150 ms

    // ---- the rig --------------------------------------------------------------------------------------

    // A snapshot the server emitted at a given tick. The harness queues these and delivers each to the client at
    // (produced wall time) + latency (+ jitter), unless its tick is dropped. Tile + StepSeq are the authoritative
    // values the client reconciles against (StepSeq rides the wire as the recipient-scoped RecipientStepSeq).
    private readonly record struct PendingSnapshot(uint ServerTick, TileCoord Tile, uint StepSeq);

    // What one rig run produced: the reconcile-outcome tallies and the per-frame render trace, plus the final
    // server/predicted state, so a test can assert on whichever it cares about.
    public sealed class RunResult
    {
        public int Matched;
        public int Corrected;
        public int Snapped;
        public int ReconcileCalls;
        public readonly List<RenderPosition> RenderTrace = new();
        public readonly List<uint> DeliveredStepSeqs = new();
        public TileCoord FinalServerTile;
        public TileCoord FinalPredictedTile;
        public uint FinalServerStepSeq;
        public uint FinalPredictedStepSeq;
        public int MaxLeadTiles;     // max Chebyshev(predicted, server) observed at snapshot time
    }

    private sealed class Rig
    {
        public required TileGrid Grid;
        public required WorldEntity Server;
        public required LocalPlayerPredictor Predictor;
        public double LatencyMs;
        public int JitterMs;
        public Random JitterRng = new(20260621);
        public HashSet<uint> DropTicks = new();
    }

    private static Rig NewRig(TileCoord start, Direction8 facing, bool clientDriven,
        IEnumerable<TileCoord>? blocked = null, double latencyMs = 50d, int jitterMs = 0,
        IEnumerable<uint>? dropTicks = null)
    {
        var grid = new TileGrid(512, 512, blocked ?? Array.Empty<TileCoord>());
        var server = new WorldEntity(1, 1, EntityKind.Player, start, facing, "Local",
            Guid.NewGuid(), ownerSession: null, isDurable: true);
        var predictor = new LocalPlayerPredictor(start, facing, CadenceMs, t => grid.IsWalkable(t), TickMs);
        predictor.SetClientDriven(clientDriven);
        return new Rig
        {
            Grid = grid,
            Server = server,
            Predictor = predictor,
            LatencyMs = latencyMs,
            JitterMs = jitterMs,
            DropTicks = dropTicks is null ? new HashSet<uint>() : new HashSet<uint>(dropTicks),
        };
    }

    // Drives one sustained, single-direction run on the wall clock for `runMs` of simulated time, holding
    // `held` the whole way. Models the full real loop: the client polls each frame (predictor.Tick), the server
    // steps once per tick (TryStep / TryCommitStep), one snapshot is produced per tick and delivered after
    // latency(+jitter) unless its tick is in DropTicks, and each delivered snapshot calibrates + reconciles the
    // predictor exactly as MmoClient does. Returns the RunResult trace/tallies.
    private static RunResult RunStraightRun(Rig rig, Direction8 held, double runMs, bool clientDriven)
    {
        var result = new RunResult();
        rig.Predictor.SetIntent(true, held, TimeSpan.Zero);

        // Snapshots produced but not yet delivered (queued for latency). Each tick produces one; we deliver any
        // whose delivery time has arrived at the current frame.
        var pending = new List<(double DeliverAtMs, PendingSnapshot Snap)>();

        // Heap-allocated once (NOT stackalloc in the frame loop, which would grow the stack per iteration over
        // thousands of frames). Holds the accepted-step directions Tick reports in UoClientDriven mode.
        var acceptedBuffer = new Direction8[8];

        // CLIENT-DRIVEN commit stream: in UoClientDriven the server's held-intent pacer is OFF — it only advances
        // on the per-step commits the CLIENT emits (one per accepted predicted step). So we don't auto-step the
        // server; instead each accepted predicted step queues a commit (its direction + the tick it stepped on)
        // that the server applies via TryCommitStep when that tick elapses. This is the faithful architecture:
        // the PREDICTOR drives, the server follows the commits. (Server-paced mode ignores this and steps in the
        // tick loop below.)
        var pendingCommits = new Queue<(double DeliverAtMs, Direction8 Dir)>();

        uint nextServerTick = 0;
        var endMs = runMs;
        // Walk the wall clock at frame granularity. At each frame: advance the server's ticks that have elapsed
        // (stepping — server-paced — or applying queued commits — client-driven — and emitting a snapshot per
        // tick), deliver any due snapshots (calibrate + reconcile), then tick + sample the predictor.
        for (var frame = 0; ; frame++)
        {
            var nowMs = frame * FrameMs;
            if (nowMs > endMs)
            {
                break;
            }

            // --- server side: step + emit a snapshot for every tick boundary that has elapsed by now ---
            while (nextServerTick * TickMs <= nowMs)
            {
                var tick = nextServerTick;
                var serverWallMs = tick * TickMs;
                if (clientDriven)
                {
                    // Apply every queued commit that has arrived at the server (uplink latency elapsed) by this
                    // tick's wall time, stamping the server's CURRENT tick — exactly as the real server processes
                    // a StepCommitRequest at the tick it dequeues it. TryCommitStep runs the identical walkability
                    // + anti-cheat-floor gate; commits emitted at the predictor's accepted-step cadence (a cooldown
                    // apart) clear the floor and land, keeping the server in lockstep with the predicted commits.
                    while (pendingCommits.Count > 0 && pendingCommits.Peek().DeliverAtMs <= serverWallMs)
                    {
                        var (_, commitDir) = pendingCommits.Dequeue();
                        rig.Server.TryCommitStep(commitDir, tick, StepCooldownTicks, 0.5, rig.Grid, out _);
                    }
                }
                else
                {
                    rig.Server.TryStep(held, tick, StepCooldownTicks, rig.Grid);
                }

                if (!rig.DropTicks.Contains(tick))
                {
                    var deliverAt = serverWallMs + rig.LatencyMs
                        + (rig.JitterMs > 0 ? rig.JitterRng.Next(-rig.JitterMs, rig.JitterMs + 1) : 0);
                    pending.Add((deliverAt, new PendingSnapshot(tick, rig.Server.Tile, rig.Server.StepSequence)));
                }

                nextServerTick++;
            }

            // --- deliver any snapshots whose (latency + jitter) delay has elapsed by now, in produced order ---
            // Sort by delivery time so jitter can't deliver out of order relative to wall time; the predictor's
            // own monotonic guards handle a stale-seq delivery defensively.
            pending.Sort(static (a, b) => a.DeliverAtMs.CompareTo(b.DeliverAtMs));
            var deliveredCount = 0;
            foreach (var (deliverAtMs, snap) in pending)
            {
                if (deliverAtMs > nowMs)
                {
                    break;
                }

                deliveredCount++;
                var receivedAt = TimeSpan.FromMilliseconds(nowMs);
                // Exactly the MmoClient.ApplySnapshot order: calibrate the tick frame, then reconcile by step-seq.
                rig.Predictor.CalibrateToServerTick(snap.ServerTick, receivedAt);
                var outcome = rig.Predictor.Reconcile(snap.Tile, snap.StepSeq, receivedAt);
                result.ReconcileCalls++;
                result.DeliveredStepSeqs.Add(snap.StepSeq);
                switch (outcome)
                {
                    case LocalPlayerPredictor.ReconcileOutcome.Matched: result.Matched++; break;
                    case LocalPlayerPredictor.ReconcileOutcome.Corrected: result.Corrected++; break;
                    case LocalPlayerPredictor.ReconcileOutcome.Snapped: result.Snapped++; break;
                }

                var lead = Math.Max(
                    Math.Abs(rig.Predictor.PredictedTile.X - snap.Tile.X),
                    Math.Abs(rig.Predictor.PredictedTile.Y - snap.Tile.Y));
                result.MaxLeadTiles = Math.Max(result.MaxLeadTiles, lead);
            }

            if (deliveredCount > 0)
            {
                pending.RemoveRange(0, deliveredCount);
            }

            // --- client render frame: tick the predictor and record the present-time render position ---
            var movedNow = TimeSpan.FromMilliseconds(nowMs);
            if (clientDriven)
            {
                // Mirror the UoClientDriven poll: Tick reports the directions of the steps ACCEPTED this frame;
                // the client emits one StepCommitRequest per accepted step. We queue each as a commit that reaches
                // the server after uplink latency (modelled as the same one-way LatencyMs), where it is applied.
                rig.Predictor.Tick(movedNow, acceptedBuffer, out var acceptedCount);
                var emit = Math.Min(acceptedCount, acceptedBuffer.Length);
                for (var i = 0; i < emit; i++)
                {
                    pendingCommits.Enqueue((nowMs + rig.LatencyMs, acceptedBuffer[i]));
                }
            }
            else
            {
                rig.Predictor.Tick(movedNow);
            }

            result.RenderTrace.Add(rig.Predictor.Sample(movedNow));
        }

        result.FinalServerTile = rig.Server.Tile;
        result.FinalPredictedTile = rig.Predictor.PredictedTile;
        result.FinalServerStepSeq = rig.Server.StepSequence;
        result.FinalPredictedStepSeq = rig.Predictor.PredictedStepSeq;
        return result;
    }

    // The render trace must glide monotonically forward along the run axis (no backward tile jumps). `axis`
    // picks the dominant component for the held direction; `sign` is +1 for increasing, -1 for decreasing. We
    // allow a tiny epsilon for floating tween noise. Returns the worst backward delta observed (<= eps == clean).
    private static double MaxBackwardStep(IReadOnlyList<RenderPosition> trace, Func<RenderPosition, double> axis, double sign)
    {
        var worst = 0d;
        var last = sign * axis(trace[0]);
        for (var i = 1; i < trace.Count; i++)
        {
            var v = sign * axis(trace[i]);
            var back = last - v; // positive == moved backward
            if (back > worst)
            {
                worst = back;
            }

            last = Math.Max(last, v);
        }

        return worst;
    }

    // Picks the monotonic-axis projector + sign for a held direction (the dominant travel component). Diagonals
    // use BOTH axes; we assert each component is monotonic in its travel sign.
    private static (Func<RenderPosition, double> Axis, double Sign)[] AxesFor(Direction8 dir)
    {
        var d = dir.Delta();
        var axes = new List<(Func<RenderPosition, double>, double)>();
        if (d.X != 0)
        {
            axes.Add((p => p.X, Math.Sign(d.X)));
        }

        if (d.Y != 0)
        {
            axes.Add((p => p.Y, Math.Sign(d.Y)));
        }

        return axes.ToArray();
    }

    // ---- INVARIANT 1: steady normal walking does NOT cap/snap (the UO5-catching guard) -----------------
    //
    // Hold a direction for a sustained run at BOTH 50 ms and 100 ms latency, in BOTH UoClientDriven and Predicted,
    // over all 8 directions. Assert: zero Corrected, zero Snapped over the whole run; the render glides
    // monotonically forward (no backward tile jump); and the predicted/server leads stay bounded & stable. This
    // is the regression guard: under the snapshot-vs-cadence mismatch (serverStepSeq flat 2-of-3 snapshots) the
    // UO5 stall counter misfired Corrected/Snapped here; on the current reverted code it stays Matched.

    public static IEnumerable<object[]> SteadyWalkCases()
    {
        foreach (var dir in new[]
                 {
                     Direction8.N, Direction8.NE, Direction8.E, Direction8.SE,
                     Direction8.S, Direction8.SW, Direction8.W, Direction8.NW,
                 })
        {
            foreach (var latency in new[] { 50d, 100d })
            {
                foreach (var clientDriven in new[] { false, true })
                {
                    yield return new object[] { dir, latency, clientDriven };
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(SteadyWalkCases))]
    public void SteadyWalk_NoCapNoSnap_RenderGlidesForward(Direction8 dir, double latencyMs, bool clientDriven)
    {
        // Start well inside a large open field so an 8-direction run never hits an edge.
        var rig = NewRig(new TileCoord(200, 200), dir, clientDriven, latencyMs: latencyMs);

        // ~3 s straight run: long enough that serverStepSeq's "flat for 2 of every 3 snapshots" cadence is
        // exercised dozens of times — the exact condition UO5 misread.
        var result = RunStraightRun(rig, dir, runMs: 3000d, clientDriven);

        // Zero corrections / snaps over the entire steady run — the core UO5 guard.
        Assert.Equal(0, result.Corrected);
        Assert.Equal(0, result.Snapped);

        // The render glides monotonically forward in every travel axis — no backward tile jump.
        foreach (var (axis, sign) in AxesFor(dir))
        {
            var worstBack = MaxBackwardStep(result.RenderTrace, axis, sign);
            Assert.True(worstBack <= 1e-6,
                $"render moved backward by {worstBack} on axis for {dir} @ {latencyMs}ms (clientDriven={clientDriven})");
        }

        // The prediction actually travelled (steps fired, no stall) and the lead over the server stayed bounded
        // and stable — never ratcheting away. At 50–100 ms latency the genuine in-flight lead is ~1–2 tiles.
        Assert.True(result.MaxLeadTiles <= 3,
            $"prediction lead ratcheted to {result.MaxLeadTiles} tiles for {dir} @ {latencyMs}ms");
        Assert.True(result.FinalServerStepSeq >= 15,
            $"server barely stepped ({result.FinalServerStepSeq}) — run did not exercise sustained walking");
    }

    // ---- INVARIANT 2: a genuine reject DOES snap (correction still happens when it should) --------------
    //
    // Walk straight into a wall the SERVER refuses but the predictor's oracle (deliberately) thinks is open, so
    // the prediction over-runs onto a tile the server never confirms. The reconcile must Correct/Snap and pull
    // the render back. This proves the guard in Invariant 1 isn't just "never correct" — it corrects when truth
    // diverges.

    [Fact]
    public void GenuineReject_SnapsAndPullsRenderBack()
    {
        // The server's authoritative grid blocks x >= 205; the avatar starts at (200,200) heading E. The
        // predictor's oracle, however, sees the field as fully open (it predicts straight through the wall),
        // so the prediction over-runs past x=204 while the server holds at the wall. The next confirms diverge
        // => Corrected/Snapped, and the predicted tile is pulled back onto the server's wall tile.
        var grid = new TileGrid(512, 512, BlockColumnFrom(205, 512));
        var server = new WorldEntity(1, 1, EntityKind.Player, new TileCoord(200, 200), Direction8.E, "Local",
            Guid.NewGuid(), ownerSession: null, isDurable: true);
        // Predictor oracle = OPEN field (mispredicts the wall), so it walks onto tiles the server refuses.
        var predictor = new LocalPlayerPredictor(new TileCoord(200, 200), Direction8.E, CadenceMs,
            _ => true, TickMs);

        var rig = new Rig { Grid = grid, Server = server, Predictor = predictor, LatencyMs = 50d };

        var result = RunStraightRun(rig, Direction8.E, runMs: 3000d, clientDriven: false);

        // Correction fired (the prediction was pulled toward the server's truth — it did NOT silently match).
        Assert.True(result.Corrected + result.Snapped > 0,
            "a genuine server reject must reconcile (Corrected/Snapped), but none fired");

        // The server held at the last open tile, x=204 (205+ is the wall).
        Assert.Equal(204, result.FinalServerTile.X);

        // The reconcile BOUNDED the over-run: instead of running unbounded into the wall (open-oracle prediction
        // alone would reach x≈221 over a 3 s run), reconcile re-anchors the predicted head to within
        // MaxInFlightLead (2) of the confirmed wall tile each snapshot. Between the final snapshot and run-end the
        // predictor can step at most ~1 more tile, so the lead over the server stays small and bounded — proof the
        // correction is pulling the render back toward the wall, not letting it ratchet through.
        var finalLead = result.FinalPredictedTile.X - result.FinalServerTile.X;
        Assert.True(finalLead >= 0 && finalLead <= 4,
            $"prediction lead over the server wall tile was {finalLead} (predicted x={result.FinalPredictedTile.X}, " +
            $"server x={result.FinalServerTile.X}); reconcile failed to bound the over-run");
    }

    // ---- INVARIANT 3: a dropped snapshot self-heals via the cumulative RecipientStepSeq -----------------
    //
    // Skip delivering one snapshot mid-run. Because RecipientStepSeq is the server's CUMULATIVE accepted-move
    // count, the NEXT delivered snapshot re-syncs the predictor — there must be no permanent desync: by the end
    // the prediction equals the server tile/seq and the run did not snap.

    [Fact]
    public void DroppedSnapshot_SelfHeals_NoPermanentDesync()
    {
        // Drop the snapshots at ticks 18, 19, 20 (mid-run, around a step-confirm boundary) so a whole 150 ms
        // confirm window's worth of snapshots is lost — the worst realistic drop burst. The cumulative
        // RecipientStepSeq on the following snapshot must re-sync with no lasting desync.
        var dropped = NewRig(new TileCoord(200, 200), Direction8.E, clientDriven: false,
            latencyMs: 50d, dropTicks: new uint[] { 18, 19, 20 });
        var droppedResult = RunStraightRun(dropped, Direction8.E, runMs: 3000d, clientDriven: false);

        // The "no permanent desync" proof: the dropped-snapshot run ends EXACTLY where a clean (no-drop) run does
        // — the lost snapshots left no lasting trace because RecipientStepSeq is cumulative, so the next delivered
        // snapshot re-syncs the predictor to the same trajectory it would have been on.
        var clean = NewRig(new TileCoord(200, 200), Direction8.E, clientDriven: false, latencyMs: 50d);
        var cleanResult = RunStraightRun(clean, Direction8.E, runMs: 3000d, clientDriven: false);

        Assert.Equal(cleanResult.FinalPredictedTile, droppedResult.FinalPredictedTile);
        Assert.Equal(cleanResult.FinalPredictedStepSeq, droppedResult.FinalPredictedStepSeq);
        Assert.Equal(cleanResult.FinalServerTile, droppedResult.FinalServerTile);

        // The drop is healed by a benign cumulative-seq re-sync, never a snap or a backward render jump.
        Assert.Equal(0, droppedResult.Snapped);
        var worstBack = MaxBackwardStep(droppedResult.RenderTrace, p => p.X, +1);
        Assert.True(worstBack <= 1e-6, $"render moved backward by {worstBack} across the dropped-snapshot re-sync");
        // The lead stays bounded across the gap (no ratchet from the missed confirms).
        Assert.True(droppedResult.MaxLeadTiles <= 3,
            $"prediction lead ratcheted to {droppedResult.MaxLeadTiles} tiles across the dropped snapshots");
    }

    // ---- a long straight run with jitter (the "+ a long straight run" the task asks for) ---------------

    [Fact]
    public void LongStraightRun_WithJitter_NoCapNoSnap_StableLead()
    {
        // A 6 s straight east run at 100 ms latency with ±15 ms snapshot-arrival jitter — the longest sustained
        // walk, the most snapshot-vs-cadence cycles. Still zero corrections/snaps and a bounded, stable lead.
        var rig = NewRig(new TileCoord(100, 100), Direction8.E, clientDriven: false,
            latencyMs: 100d, jitterMs: 15);

        var result = RunStraightRun(rig, Direction8.E, runMs: 6000d, clientDriven: false);

        Assert.Equal(0, result.Corrected);
        Assert.Equal(0, result.Snapped);
        var worstBack = MaxBackwardStep(result.RenderTrace, p => p.X, +1);
        Assert.True(worstBack <= 1e-6, $"render moved backward by {worstBack} on the long jittered run");
        Assert.True(result.MaxLeadTiles <= 3, $"lead ratcheted to {result.MaxLeadTiles} on the long jittered run");
        // ~6 s / 150 ms cadence ≈ 40 steps actually fired.
        Assert.True(result.FinalServerStepSeq >= 30, $"long run barely stepped: {result.FinalServerStepSeq}");
    }

    // ---- INVARIANT 4 (NET2): a dropped UO COMMIT recovers from the redundant batch window — no speed-up ----
    //
    // The GodotB symptom NET2 fixes: under loss the per-step commits used to retransmit RELIABLE in a BATCH that
    // the server's cooldown gate rejected together → the local avatar sped up + desynced. NET2 ships commits
    // redundant-UNRELIABLE (each batch repeats a window of recent commits), so a dropped commit recovers from a
    // LATER batch — applied ONCE at the server (cursor dedup), at cadence (cooldown gate). This test drives the
    // UoClientDriven predictor, emits each accepted step as a redundant StepCommitBatch (the real client path),
    // DROPS a run of those batches on the uplink, and asserts the server still steps to the SAME tile/StepSeq as
    // a no-loss run — recovered from the window, never double-applied (no speed-up).
    [Fact]
    public void UoCommitDrop_RecoversFromRedundantBatchWindow_NoSpeedUp()
    {
        // Hold east for 2.4 s (banking ~16 steps a cadence apart), then RELEASE so the commit tail fully
        // delivers + applies through the normal tick loop by run-end (no artificial drain). Drop a SINGLE
        // commit batch mid-run (frame 600 ≈ 2.6 s in is past intent-end, so pick mid-stream frame 300): the
        // dropped commit's sequence is re-carried by the NEXT batch's window (a cadence-spaced packet), so the
        // server picks it up deduped and at cadence — the speed-up/desync the old reliable retransmit caused
        // does not happen. Drop ~30% of the batches in a mid-run window to model typical loss.
        var droppedResult = RunUoCommitStream(
            start: new TileCoord(200, 200), held: Direction8.E, runMs: 4000d, holdUntilMs: 2400d,
            dropBatchOnFrame: f => f % 3 == 0 && f >= 200 && f < 320);

        // A clean (no-loss) run of the identical stream is the oracle: the lossy run must land EXACTLY there.
        var cleanResult = RunUoCommitStream(
            start: new TileCoord(200, 200), held: Direction8.E, runMs: 4000d, holdUntilMs: 2400d,
            dropBatchOnFrame: _ => false);

        // Recovery: despite the lost commit packets, the server reached the SAME confirmed tile + step count as
        // the no-loss run — every dropped commit was recovered from a later packet's redundancy window.
        Assert.Equal(cleanResult.ServerTile, droppedResult.ServerTile);
        Assert.Equal(cleanResult.ServerStepSeq, droppedResult.ServerStepSeq);

        // No speed-up: the server applied EXACTLY as many steps as the predictor banked — not one more. The old
        // reliable retransmit re-delivered the lost commits BUNCHED, which the cooldown gate then mis-paced;
        // redundant-unreliable + cursor dedup applies each commit once, spread out, so server == predictor.
        Assert.Equal(droppedResult.PredictedStepSeq, droppedResult.ServerStepSeq);
        Assert.Equal(droppedResult.PredictedTile, droppedResult.ServerTile);
    }

    private readonly record struct UoCommitRunResult(
        TileCoord ServerTile, uint ServerStepSeq, TileCoord PredictedTile, uint PredictedStepSeq);

    // Drives the UoClientDriven commit path with NET2's redundant-unreliable batch delivery and optional uplink
    // batch loss. The predictor steps each frame (holding `held` until holdUntilMs, then released so the tail
    // drains through the normal loop); every accepted step mints a fresh sequence on a shared cursor and is
    // recorded in an 8-deep ring; each frame that produced commits ships ONE redundant StepCommitBatch (head +
    // window of prior ring commits as deltas) onto the uplink unless dropBatchOnFrame says to drop it. The
    // server reconstructs fresh commits via the REAL GameServer.ExtractFreshStepCommits (cursor dedup) and
    // applies each through the REAL WorldEntity.TryCommitStep at the tick it arrives, ONE per tick (the cooldown
    // gate paces it). Mirrors the production client (RecordStepCommit/SendStepCommitBatch) + server
    // (HandleStepCommitBatch) exactly.
    private static UoCommitRunResult RunUoCommitStream(
        TileCoord start, Direction8 held, double runMs, double holdUntilMs, Func<int, bool> dropBatchOnFrame)
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
        var ring = new List<(uint Seq, Direction8 Dir)>();
        uint moveSeq = 0;
        uint serverCursor = 0;

        // A delivered batch is the wire payload (head + window) plus the wall time it reaches the server.
        var pendingBatches = new List<(double DeliverAtMs, Mmo.Shared.Protocol.StepCommitBatchMessage Batch)>();
        var acceptedBuffer = new Direction8[8];
        uint nextServerTick = 0;

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

            // --- server: at each elapsed tick, apply AT MOST ONE arrived+fresh commit (the cooldown gate paces
            // the rest), exactly as the live tick loop processes the commit stream one accepted step per cadence.
            while (nextServerTick * TickMs <= nowMs)
            {
                var tick = nextServerTick;
                var serverWallMs = tick * TickMs;

                // Gather the fresh commits that have ARRIVED by this tick, deduped + ascending across all
                // delivered batches (the real ExtractFreshStepCommits + cursor dedup, fed every arrived batch).
                var arrivedThisTick = new SortedSet<uint>();
                var dirBySeq = new Dictionary<uint, Direction8>();
                foreach (var (deliverAt, batch) in pendingBatches)
                {
                    if (deliverAt > serverWallMs)
                    {
                        continue;
                    }

                    foreach (var (seq, dir) in GameServer.ExtractFreshStepCommits(batch, serverCursor))
                    {
                        arrivedThisTick.Add(seq);
                        dirBySeq[seq] = dir;
                    }
                }

                // Apply the oldest fresh arrived commit, gated by the REAL cooldown floor (so the rate can never
                // exceed cadence). The next one applies on a later tick once its cooldown elapses — this is the
                // anti-speedhack pacing that, under reliable retransmit, the bunched batch fought; under the
                // redundant window each commit is already cadence-spaced, so it lands cleanly.
                if (arrivedThisTick.Count > 0)
                {
                    var seq = arrivedThisTick.Min;
                    if (server.TryCommitStep(dirBySeq[seq], tick, StepCooldownTicks, 0.5, grid, out _))
                    {
                        serverCursor = seq;
                    }
                }

                nextServerTick++;
            }

            // --- client: tick the predictor; emit the accepted steps as ONE redundant batch (unless dropped) ---
            predictor.Tick(TimeSpan.FromMilliseconds(nowMs), acceptedBuffer, out var acceptedCount);
            var emit = Math.Min(acceptedCount, acceptedBuffer.Length);
            for (var i = 0; i < emit; i++)
            {
                ring.Add((++moveSeq, acceptedBuffer[i]));
                if (ring.Count > ringCap)
                {
                    ring.RemoveAt(0);
                }
            }

            if (emit > 0 && !dropBatchOnFrame(frame))
            {
                var head = ring[^1];
                var window = new List<Mmo.Shared.Protocol.StepCommitWindowEntry>();
                for (var i = ring.Count - 2; i >= 0; i--)
                {
                    var delta = head.Seq - ring[i].Seq;
                    if (delta is > 0 and <= byte.MaxValue)
                    {
                        window.Add(new Mmo.Shared.Protocol.StepCommitWindowEntry((byte)delta, ring[i].Dir));
                    }
                }

                var batch = new Mmo.Shared.Protocol.StepCommitBatchMessage(head.Seq, head.Dir, window);
                pendingBatches.Add((nowMs + latencyMs, batch));
            }
        }

        // Drain any in-flight batches the run-end cut off (so the comparison is on the fully-delivered stream).
        var drainTick = nextServerTick;
        foreach (var (_, batch) in pendingBatches)
        {
            foreach (var (seq, dir) in GameServer.ExtractFreshStepCommits(batch, serverCursor))
            {
                if (seq > serverCursor)
                {
                    serverCursor = seq;
                    server.TryCommitStep(dir, drainTick, StepCooldownTicks, 0.5, grid, out _);
                }

                drainTick += StepCooldownTicks; // space the drained commits a cadence apart so each clears the floor
            }
        }

        return new UoCommitRunResult(
            server.Tile, server.StepSequence, predictor.PredictedTile, predictor.PredictedStepSeq);
    }

    // ====================================================================================================
    // UO5 RE-ATTEMPT SLOT (out of scope for TEST1 — left red on purpose):
    //
    //   The "frame-drop overshoot converges back" test belongs HERE, driven against this same rig. Model it as:
    //   run a steady straight run, then inject a single large client-side frame stall (skip several Tick frames
    //   so the predictor catches up multiple steps in ONE Tick and over-shoots the server), and assert the
    //   reconcile converges the over-shoot back DOWN to the server's authoritative tile within a bounded number
    //   of snapshots (the render settles forward/back without a permanent ratchet).
    //
    //   It is NOT added here because that convergence is the bug UO5 must FIX — on the current reverted code the
    //   over-shoot does not fully converge, so the assertion is RED. The UO5 re-attempt adds it, watches it go
    //   red on the current code, applies its fix, and drives it green against this rig (RunStraightRun + a
    //   frame-stall hook on the client Tick loop). Everything it needs — server-step model, snapshot
    //   latency/jitter/drop, outcome tallies, render trace — is already in this harness.
    // ====================================================================================================

    private static IEnumerable<TileCoord> BlockColumnFrom(int x, int height)
    {
        for (var y = 0; y < height; y++)
        {
            yield return new TileCoord(x, y);
        }
    }
}
