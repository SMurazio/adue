using System;
using Mmo.Client.Core.Continuous;
using Mmo.Shared.Domain;
using Xunit;
using Xunit.Abstractions;

namespace Mmo.Client.Core.Tests;

// CONTINUOUS MIGRATION — THE PERSISTENT SOFT RUBBERBAND ON PLAIN MOVEMENT (snapCount=0, ~1.5 tiles).
//
// LIVE SYMPTOM: on feat/continuous-migration the LOCAL player's render snaps BACK during plain continuous
// movement on an empty map — a SOFT correction (under the 4u snap threshold). It felt PERFECT on
// exp/continuous-movement with the SAME predictor. The dt-budget bug (676f1fa) was already fixed; the snap
// PERSISTS, so it is something else.
//
// THE MEASURED WIRING DIFFERENCE (this test pins it):
//
//   EXPERIMENT server (Mmo.Tools.ContinuousServer): broadcasts a ContinuousState EVERY tick carrying the LIVE
//   authoritative position (state.Mover.X) paired with the CURRENT LastInputSeq. Position and ack ALWAYS advance
//   together → the predictor always re-bases onto the up-to-date truth → with no loss the replay reproduces the
//   live prediction byte-for-byte → ZERO correction. (Modelled by ContinuousReconcileHarnessTests, all green.)
//
//   MIGRATION server: delta-compresses. The recipient's OWN entity is included in the snapshot payload only when
//   recipient.HasAckedCurrentRevision(entity) is FALSE — i.e. only when StateRevision changed. And
//   WorldEntity.ApplyResolvedMove bumps StateRevision ONLY WHEN THE ROUNDED TILE CROSSES (the carried-over tile
//   cadence: "R1: do NOT bump every sub-tile tick"). So while the player moves CONTINUOUSLY between tile
//   boundaries (4 of every 5 ticks at 4u/s), its StateRevision does NOT change → it is DELTA'D OUT of its own
//   snapshot → the client takes the keepalive / delta'd-out reconcile path (MmoClient.ApplySnapshot ~1008-1027):
//
//       localEntity.ApplySnapshot(localEntity.Position, ...);   // re-apply the LAST-KNOWN (stale) confirmed pos
//       ReconcileLocalPredictor(localEntity);                    // Reconcile(localEntity.Position, _lastInputSeq)
//
//   But the snapshot HEADER still rides recipient.LastInputSeq, which advances EVERY integrated input (every
//   tick — TryBeginMoveInput), regardless of tile crossing. So the client reconciles against:
//       base  = the STALE confirmed position from the LAST tile crossing (delta'd out since), and
//       seq   = the FRESH LastInputSeq (the server has integrated MANY more inputs since that position).
//   Reconcile drops every buffered input with seq <= the fresh seq (the server "acked" them), then replays only
//   the few still-unacked inputs from the STALE base. The motion the server integrated BETWEEN the stale tile
//   crossing and now was dropped from the buffer but is NOT reflected in the stale base → the recomputed present
//   UNDERSHOOTS the true position by exactly that between-crossings motion → a persistent BACKWARD correction
//   every non-tile-crossing tick → the SOFT rubberband (bounded by ~1 tile of motion, snapCount=0). This is the
//   exact thing the experiment never hit because it never delta'd out a moving player.
//
// WHY THE EXISTING HARNESS MISSED IT: ContinuousReconcileHarnessTests emits a snapshot with the LIVE server pos
// EVERY tick (the experiment model) — it never models the tile-gated revision / keepalive-reconcile-against-
// stale-pos path that the migration server actually has. So it inherited the experiment's wiring and stayed
// green while the live migration rubberbands (the review-independence lesson, verbatim).
public sealed class ContinuousTileGatedReconcileTests
{
    private readonly ITestOutputHelper _out;
    public ContinuousTileGatedReconcileTests(ITestOutputHelper output) => _out = output;

    private const double ServerTickSeconds = 1.0d / 20d;   // 20Hz authoritative integration
    private const double ClientFrameSeconds = 1.0d / 144d; // ~144Hz client poll/predict/render
    private const double Radius = CollisionDefaults.BodyRadius;
    private const double Speed = 1000d / 250d;             // 4.0 u/s — 1 tile every 5 server ticks

    // CONTROL: the EXPERIMENT wiring (reconcile against the LIVE pos every tick, paired with the matching seq).
    // No loss, open field → ZERO correction. This is the "felt perfect" baseline.
    [Fact]
    public void Experiment_LivePosEveryTick_NoCorrection()
    {
        var sim = new Sim(tileGatedRevision: false);
        sim.RunHeldEast(seconds: 4d);

        _out.WriteLine($"[EXPERIMENT live-pos-every-tick] maxCorrection={sim.MaxCorrectionUnits:F4}u " +
            $"maxRenderRetreat={sim.MaxRenderRetreatUnits:F4}u snapCount={sim.SnapCount}");

        Assert.Equal(0, sim.SnapCount);
        Assert.True(sim.MaxCorrectionUnits <= 0.1d,
            $"the experiment wiring opened a correction it never had live: {sim.MaxCorrectionUnits:F4}u");
        Assert.True(sim.MaxRenderRetreatUnits <= 0.02d,
            $"render retreated under the experiment wiring: {sim.MaxRenderRetreatUnits:F4}u");
    }

    // THE REPRO: the MIGRATION wiring — StateRevision (and thus the payload position) updates ONLY on a rounded-
    // tile crossing, while the header LastInputSeq advances every tick. Between crossings the client reconciles
    // against the STALE last-confirmed position with the FRESH seq → a persistent backward UNDERSHOOT correction.
    // Same predictor, same no-loss honest play, same empty map — ONLY the tile-gated delta differs.
    [Fact]
    public void Migration_TileGatedRevision_OpensPersistentBackwardCorrection()
    {
        var sim = new Sim(tileGatedRevision: true);
        sim.RunHeldEast(seconds: 4d);

        _out.WriteLine($"[MIGRATION tile-gated] maxCorrection={sim.MaxCorrectionUnits:F4}u " +
            $"maxRenderRetreat={sim.MaxRenderRetreatUnits:F4}u backwardCorrections={sim.BackwardCorrectionCount} " +
            $"snapCount={sim.SnapCount} maxStaleBaseLagUnits={sim.MaxStaleBaseLagUnits:F4}u");

        // The correction is a SOFT one (snapCount=0) — matches the live motion.snapCount=0.
        Assert.Equal(0, sim.SnapCount);
        // The tile-gated delta opens a real, repeated BACKWARD correction the experiment wiring never had: the
        // reconcile re-bases onto a stale position and yanks the render back toward it many times across the run.
        Assert.True(sim.MaxCorrectionUnits >= 0.3d,
            $"expected a real backward correction from the stale-base reconcile; got {sim.MaxCorrectionUnits:F4}u");
        Assert.True(sim.BackwardCorrectionCount >= 3,
            $"expected REPEATED backward corrections (the rubberband); got {sim.BackwardCorrectionCount}");
        // The reconcile re-bases onto a STALE position lagging the live truth — the undershoot baked in every
        // non-crossing tick. (Orchestrator correction: the first cut asserted the PER-FRAME render retreat >= 0.1u,
        // but that's ~0.005u here — the offset-decay spreads each ~0.8u backward correction over many frames, so the
        // user-visible rubberband is the CONSTANT drag of the 48 repeated backward corrections re-basing onto this
        // stale lag, NOT a single-frame snap. The faithful signal is the stale-base lag + the backward-correction count.)
        Assert.True(sim.MaxStaleBaseLagUnits >= 0.3d,
            $"expected the reconcile to re-base onto a stale position lagging the truth; got {sim.MaxStaleBaseLagUnits:F4}u");
    }

    // THE FIX VALIDATION (modelled): if the server bumped the recipient's revision EVERY tick the position changed
    // (i.e. included the moving local player in its own snapshot every sub-tile tick — OR the client only
    // reconciled the local player when a FRESH confirmed position actually arrived), the reconcile always re-bases
    // onto the live truth and the correction collapses to ~0. This is the experiment behaviour restored.
    [Fact]
    public void Fix_PositionRidesEveryTick_CorrectionCollapses()
    {
        // tileGatedRevision:false is exactly "the moving local player rides its own snapshot every tick" — the fix.
        var sim = new Sim(tileGatedRevision: false);
        sim.RunHeldEast(seconds: 4d);
        Assert.True(sim.MaxCorrectionUnits <= 0.1d, $"fix did not collapse the correction: {sim.MaxCorrectionUnits:F4}u");
        Assert.True(sim.BackwardCorrectionCount == 0, $"fix still has backward corrections: {sim.BackwardCorrectionCount}");
    }

    // The faithful sim: REAL ContinuousPredictor on the client; a 20Hz authoritative server integrating the
    // received per-frame inputs by their own dt (open field). The ONLY toggle is whether the server's snapshot
    // carries the live pos every tick (experiment) or only re-publishes the confirmed pos on a rounded-tile
    // crossing (migration). Zero network latency (the live repro was on an empty LOCAL map — latency is NOT the
    // cause). The header LastInputSeq always rides the current integrate cursor (every tick), per the real server.
    private sealed class Sim
    {
        private readonly bool _tileGatedRevision;
        private readonly ContinuousPredictor _predictor;

        // Authoritative server state.
        private double _serverX;
        private uint _serverLastInputSeq;

        // The last position the client has CONFIRMED (re-based onto). Under the migration wiring this only updates
        // when the rounded tile crosses; otherwise the keepalive path re-applies this same stale value.
        private double _confirmedX;
        private int _lastPublishedTileX;

        // Pending inputs (client->server), zero latency: integrated at the server tick following their send.
        private readonly System.Collections.Generic.Queue<(double At, uint Seq, double Dt)> _inputs = new();

        private double _clock;
        private double _nextServerTick = ServerTickSeconds;
        private double _prevRenderX;
        private bool _haveRenderBaseline;

        public Sim(bool tileGatedRevision)
        {
            _tileGatedRevision = tileGatedRevision;
            _predictor = new ContinuousPredictor(Speed, 0d, 0d, blocked: null, radius: Radius);
            _confirmedX = 0d;
            _lastPublishedTileX = 0;
            _prevRenderX = _predictor.RenderX;
        }

        public double MaxCorrectionUnits { get; private set; }
        public double MaxRenderRetreatUnits { get; private set; }
        public int BackwardCorrectionCount { get; private set; }
        public int SnapCount { get; private set; }
        public double MaxStaleBaseLagUnits { get; private set; }

        public void RunHeldEast(double seconds)
        {
            var end = _clock + seconds;
            while (_clock < end)
            {
                // PREDICT this frame with the frame dt + "send" the input (zero latency, polled next server tick).
                var seq = _predictor.PredictAndBuffer(1d, 0d, ClientFrameSeconds);
                _inputs.Enqueue((_clock, seq, ClientFrameSeconds));

                ServerCatchUp();

                _predictor.AdvanceRender(ClientFrameSeconds);

                // Track the visible render retreat (the snap-back the user sees).
                if (_haveRenderBaseline)
                {
                    var retreat = _prevRenderX - _predictor.RenderX;
                    if (retreat > MaxRenderRetreatUnits)
                    {
                        MaxRenderRetreatUnits = retreat;
                    }
                }
                _prevRenderX = _predictor.RenderX;
                _haveRenderBaseline = true;

                _clock += ClientFrameSeconds;
            }
        }

        private void ServerCatchUp()
        {
            while (_nextServerTick <= _clock)
            {
                IntegrateDueInputs(_nextServerTick);
                ReconcileFromSnapshot();
                _nextServerTick += ServerTickSeconds;
            }
        }

        private void IntegrateDueInputs(double upTo)
        {
            while (_inputs.Count > 0 && _inputs.Peek().At <= upTo)
            {
                var input = _inputs.Dequeue();
                _serverLastInputSeq = input.Seq;      // the cursor advances EVERY integrated input (every tick)
                _serverX += Speed * input.Dt;         // open-field east, honest dt (no budget bite after 676f1fa)
            }
        }

        // The server emits a snapshot for this tick. The HEADER always carries the fresh LastInputSeq. The PAYLOAD
        // position is gated:
        //   * experiment (tileGatedRevision=false): the live _serverX rides every tick → confirmed = live truth.
        //   * migration  (tileGatedRevision=true):  the position only re-publishes when the ROUNDED TILE crosses
        //     (StateRevision bump). Between crossings the local player is delta'd out → the client keepalive path
        //     re-applies the STALE _confirmedX. Either way the predictor reconciles against (_confirmedX, freshSeq).
        private void ReconcileFromSnapshot()
        {
            if (!_tileGatedRevision)
            {
                _confirmedX = _serverX; // experiment: the live authoritative pos rides every tick.
            }
            else
            {
                var tileX = (int)Math.Round(_serverX); // rounded-tile crossing test (matches WorldVector.ToTileRounded on X)
                if (tileX != _lastPublishedTileX)
                {
                    _confirmedX = _serverX; // a tile crossing re-publishes the (now-current) confirmed position.
                    _lastPublishedTileX = tileX;
                }
                // else: delta'd out — _confirmedX stays the stale last-crossing value; the keepalive path re-uses it.
            }

            // How far the (possibly stale) confirmed base lags the live server truth — the undershoot the reconcile bakes in.
            var staleLag = Math.Abs(_serverX - _confirmedX);
            if (staleLag > MaxStaleBaseLagUnits)
            {
                MaxStaleBaseLagUnits = staleLag;
            }

            var renderBefore = _predictor.RenderX;

            // The reconcile the migration runs on EVERY snapshot (in-snapshot OR keepalive): base = confirmed pos,
            // ack = the FRESH header seq. Under the tile-gate the base is stale but the seq is current → undershoot.
            _predictor.Reconcile(new WorldVector(_confirmedX, 0d), _serverLastInputSeq);

            var correction = _predictor.LastCorrectionUnits;
            if (correction > MaxCorrectionUnits)
            {
                MaxCorrectionUnits = correction;
            }

            // A backward correction = the reconcile pulled the predicted present BACK (toward the stale base).
            if (_predictor.PredictedX < renderBefore - 1e-6)
            {
                BackwardCorrectionCount++;
            }

            if (_predictor.RenderVsPredictedUnits > 4.0d || correction > 4.0d)
            {
                SnapCount++;
            }
        }
    }
}
