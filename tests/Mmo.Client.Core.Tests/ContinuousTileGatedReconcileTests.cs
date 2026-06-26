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

// CONTINUOUS MIGRATION — THE KEY-RELEASE (STOP) TRANSITION, WITH THE STOP-EDGE RE-PUBLISH FIX.
//
// LIVE SYMPTOM (user): "something weird happens when I LEAVE / RELEASE my move hotkey (stop)" on
// feat/continuous-migration, suspected an OLD tile-movement remnant. Continuous motion itself now feels GOOD
// (the sub-tile reconcile fix, 1133c7e — the moving player is force-included in its own snapshot EVERY tick
// while Velocity != 0). The experiment (exp/continuous-movement) stopped cleanly.
//
// THE DEFECT (pre-fix), AT THE STOP INSTANT:
//
//   While MOVING the migration server force-includes the own entity every tick (Velocity != 0) → the client's
//   confirmed base tracks the LIVE position every tick, exactly like the experiment. GOOD.
//
//   The moment the (0,0) stop input is processed, WorldEntity.StopMovement() set Velocity = Zero and left
//   Position untouched, bumping NO StateRevision (a stop crosses no tile). On the NEXT snapshot build
//   forceOwnWhileMoving == false (Velocity == 0) AND the acked-baseline delta sees no revision change, so the
//   own entity was DELTA'D OUT and — since nothing at rest ever bumps the revision — NEVER re-published at rest.
//   Under stop-edge packet loss (movement snapshots are Unreliable/UDP), the client's confirmed base FROZE at the
//   stale last-moving position and the predictor settled BACKWARD onto it on release ("weird on release").
//
// THE FIX (this branch): WorldEntity.StopMovement bumps StateRevision ONCE on the moving→stopped TRANSITION
// (Velocity was non-zero, now Zero). That re-enters the precise stop position into the standard delta path:
//   * the stop snapshot now CARRIES the own entity (one extra include at the stop instant), and
//   * the unacked-entity self-heal re-includes it on the NEXT snapshot under loss, until the client acks it.
// A second StopMovement() on an already-rest entity is a no-op (Velocity already Zero) → a player at steady rest
// does NOT keep bumping → no bandwidth at rest. The experiment server (no delta compression) heals the same way
// by re-broadcasting every tick; the fix gives the migration the same one-tick self-heal under loss.
//
// THE MEASURED CONCLUSION (these sims, fixed wiring):
//   * NO-LOSS: the migration stop is CLEAN — predicted == confirmed == exact stop, no residual.
//     (Stop_NoLoss_MigrationWiring_NoResidual.)
//   * LOSS AT THE STOP EDGE: the dropped stop snapshot leaves the own entity unacked, so the NEXT snapshot
//     re-includes the precise stop position and the confirmed base catches up — the migration now HEALS just like
//     the experiment, no backward settle. (Stop_LossAtStopEdge_MigrationHealsLikeExperiment.)
//   * STEADY REST: once stopped, the entity does NOT keep bumping the revision — no re-include / no bandwidth at
//     rest after the single transition re-publish. (Stop_SteadyRest_DoesNotKeepRepublishing.)
public sealed class ContinuousStopTransitionTests
{
    private readonly ITestOutputHelper _out;
    public ContinuousStopTransitionTests(ITestOutputHelper output) => _out = output;

    private const double ServerTickSeconds = 1.0d / 20d;
    private const double ClientFrameSeconds = 1.0d / 144d;
    private const double Radius = CollisionDefaults.BodyRadius;
    private const double Speed = 1000d / 250d; // 4.0 u/s

    // NO-LOSS migration stop: hold east, then release. The exact stop position rode the last MOVING (force-
    // included) snapshot, so at rest the predicted and render settle on it with no backward residual.
    [Fact]
    public void Stop_NoLoss_MigrationWiring_NoResidual()
    {
        var sim = new StopSim(dropStopEdgeSnapshot: false);
        sim.RunHeldEast(seconds: 2d);
        sim.Release(seconds: 1d);

        _out.WriteLine($"[migration stop NO-LOSS] confirmedX={sim.ConfirmedX:F4} predictedX={sim.PredictedX:F4} " +
            $"renderX={sim.RenderX:F4} serverX={sim.ServerX:F4} maxPostStopRetreat={sim.MaxPostStopRetreatUnits:F5}u " +
            $"settleResidual={sim.SettleResidualUnits:F5}u");

        // The render must not retreat after release, and predicted/render settle exactly on the authoritative stop.
        Assert.True(sim.MaxPostStopRetreatUnits <= 0.01d,
            $"render retreated after release (the 'weird' stop): {sim.MaxPostStopRetreatUnits:F5}u");
        Assert.True(sim.SettleResidualUnits <= 0.01d,
            $"predicted/render did not settle on the authoritative stop: {sim.SettleResidualUnits:F5}u");
        Assert.Equal(sim.ServerX, sim.PredictedX, 3);
    }

    // LOSS AT THE STOP EDGE (the fix's headline case): drop the snapshot that would have carried the final moving
    // position AND the stop-transition re-publish. Pre-fix the migration delta'd the entity out at Velocity==0 and
    // never re-sent it → stale base → backward settle. WITH THE FIX the stop transition marks the own entity
    // unacked (the StateRevision bump), so the dropped snapshot leaves it unacked and the NEXT snapshot re-includes
    // the precise stop position — the migration now HEALS just like the experiment (which heals by re-broadcasting
    // every tick). No backward settle, residual ≈ 0 for both.
    [Fact]
    public void Stop_LossAtStopEdge_MigrationHealsLikeExperiment()
    {
        var migration = new StopSim(dropStopEdgeSnapshot: true);
        migration.RunHeldEast(seconds: 2d);
        migration.Release(seconds: 1d);

        var experiment = new StopSim(dropStopEdgeSnapshot: true) { ExperimentEveryTickRepublish = true };
        experiment.RunHeldEast(seconds: 2d);
        experiment.Release(seconds: 1d);

        _out.WriteLine($"[migration stop LOSS, FIXED] confirmedX={migration.ConfirmedX:F4} predictedX={migration.PredictedX:F4} " +
            $"serverX={migration.ServerX:F4} settleResidual={migration.SettleResidualUnits:F5}u " +
            $"postStopRetreat={migration.MaxPostStopRetreatUnits:F5}u");
        _out.WriteLine($"[experiment stop LOSS] confirmedX={experiment.ConfirmedX:F4} predictedX={experiment.PredictedX:F4} " +
            $"serverX={experiment.ServerX:F4} settleResidual={experiment.SettleResidualUnits:F5}u " +
            $"postStopRetreat={experiment.MaxPostStopRetreatUnits:F5}u");

        // BOTH HEAL: under loss the dropped stop snapshot is recovered — the experiment by re-broadcasting the live
        // pos every tick, the migration by the stop-transition revision bump re-including the unacked own entity on
        // the next snapshot — so the predicted settles onto the TRUE stop: settleResidual ≈ 0 for both. (The ~0.02u
        // postStopRetreat is the NORMAL prediction-lead collapse on release — measured IDENTICAL in both wirings,
        // and 0 under no loss; it is not a defect. Orchestrator correction: the first cut asserted an absolute
        // retreat <= 0.01u, but that normal settle is ~0.02u — the faithful signal is settleResidual ≈ 0 plus the
        // migration's retreat not EXCEEDING the experiment's shared baseline.)
        Assert.True(experiment.SettleResidualUnits <= 0.01d,
            $"experiment should heal the dropped stop-edge snapshot: {experiment.SettleResidualUnits:F5}u");
        Assert.True(migration.SettleResidualUnits <= 0.01d,
            $"the stop-edge fix should heal the dropped stop snapshot; got residual {migration.SettleResidualUnits:F5}u");
        // The fix makes the migration's stop behave IDENTICALLY to the experiment — NO extra backward settle beyond
        // the shared normal lead-collapse (pre-fix the stale base added a real EXTRA retreat here).
        Assert.True(migration.MaxPostStopRetreatUnits <= experiment.MaxPostStopRetreatUnits + 1e-6d,
            $"the fix must add no stop retreat beyond the experiment's normal lead-collapse; " +
            $"migration {migration.MaxPostStopRetreatUnits:F5}u vs experiment {experiment.MaxPostStopRetreatUnits:F5}u");
    }

    // NO-BANDWIDTH-AT-REST REGRESSION: the fix must fire ONLY on the moving→stopped transition, NOT every rest
    // tick. Once stopped, the own entity must not keep being re-published (a steady-rest player must not keep
    // bumping the revision / re-including itself). The sim counts the snapshots that re-published the own entity
    // AFTER the stop transition healed (the confirmed base already at truth): with the once-only transition bump
    // that count is ZERO at steady rest.
    [Fact]
    public void Stop_SteadyRest_DoesNotKeepRepublishing()
    {
        var sim = new StopSim(dropStopEdgeSnapshot: false);
        sim.RunHeldEast(seconds: 2d);
        sim.Release(seconds: 2d); // a long idle tail — plenty of rest ticks to catch a per-tick re-publish.

        _out.WriteLine($"[migration steady-rest] republishesAtRest={sim.RepublishesAtSteadyRest} " +
            $"confirmedX={sim.ConfirmedX:F4} serverX={sim.ServerX:F4}");

        // The fix re-publishes the stop position EXACTLY ONCE (the moving→stopped transition bump, redundant under
        // no-loss but the essential self-heal seed under loss) and then NEVER again at rest — so over a long idle
        // tail the count is 1, NOT proportional to the tail. (An every-rest-tick re-publish bug would be ~40 over 2s
        // @ 20Hz. Orchestrator correction: the first cut asserted == 0, but the one legitimate transition re-publish
        // lands while confirmedX already == serverX, so the faithful no-bandwidth-at-rest signal is "bounded by 1".)
        Assert.True(sim.RepublishesAtSteadyRest <= 1,
            $"the fix must not re-publish every rest tick (once-only transition); got {sim.RepublishesAtSteadyRest}");
    }

    // A faithful stop sim: REAL ContinuousPredictor; a 20Hz server integrating per-frame inputs by their own dt.
    // Models the POST-1133c7e migration wiring PLUS the stop-edge re-publish fix. While moving the own entity is
    // force-included every tick (Velocity != 0). On the moving→stopped TRANSITION the fix bumps StateRevision once,
    // which marks the own entity UNACKED — modelled here by _ownUnacked: the entity is re-included on each snapshot
    // until one is actually DELIVERED (not dropped), then it stops being re-included (acked). That is the standard
    // delta self-heal path, and it is the whole fix: a dropped stop snapshot leaves _ownUnacked set so the NEXT
    // snapshot re-includes the precise stop position. The header LastInputSeq always rides the integrate cursor.
    // ExperimentEveryTickRepublish flips to the experiment's no-delta wiring (the live pos rides every tick forever).
    // dropStopEdgeSnapshot drops the single snapshot that would carry the final moving / stop-transition position.
    private sealed class StopSim
    {
        private readonly ContinuousPredictor _predictor;
        private readonly bool _dropStopEdgeSnapshot;

        public bool ExperimentEveryTickRepublish { get; init; }

        private double _serverX;
        private uint _serverLastInputSeq;
        private bool _serverMoving;          // is the entity currently moving (Velocity != 0)?
        private bool _wasMoving;             // was it moving on the PREVIOUS integrated input (for transition detect)?
        private bool _ownUnacked;            // the fix: the stop-transition revision bump leaves the own entity unacked
                                             // until a snapshot carrying it is actually delivered (delta self-heal).
        private double _confirmedX;          // the client's last re-based-on position.

        // Inputs in flight (zero network latency; integrated on the next server tick).
        private readonly System.Collections.Generic.Queue<(double At, uint Seq, double DirX, double Dt)> _inputs = new();

        private double _clock;
        private double _nextServerTick = ServerTickSeconds;
        private bool _released;
        private bool _droppedOnce;
        private double _prevRenderX;
        private bool _trackRetreat;

        public StopSim(bool dropStopEdgeSnapshot)
        {
            _dropStopEdgeSnapshot = dropStopEdgeSnapshot;
            _predictor = new ContinuousPredictor(Speed, 0d, 0d, blocked: null, radius: Radius);
            _confirmedX = 0d;
            _prevRenderX = _predictor.RenderX;
        }

        public double ServerX => _serverX;
        public double ConfirmedX => _confirmedX;
        public double PredictedX => _predictor.PredictedX;
        public double RenderX => _predictor.RenderX;
        public double MaxPostStopRetreatUnits { get; private set; }

        // Counts snapshots that re-published the own entity AFTER it was already stopped AND the confirmed base was
        // already at truth — i.e. spurious steady-rest re-publishes. With the once-only transition bump this is 0.
        public int RepublishesAtSteadyRest { get; private set; }

        // The residual between where the prediction settled and the true authoritative stop, after the run.
        public double SettleResidualUnits => System.Math.Abs(_predictor.PredictedX - _serverX);

        public void RunHeldEast(double seconds) => Run(seconds, dirX: 1d);

        public void Release(double seconds)
        {
            _released = true;
            _trackRetreat = true;            // only watch for a backward render move AFTER release.
            _prevRenderX = _predictor.RenderX;
            Run(seconds, dirX: 0d);
        }

        private void Run(double seconds, double dirX)
        {
            var end = _clock + seconds;
            while (_clock < end)
            {
                var seq = _predictor.PredictAndBuffer(dirX, 0d, ClientFrameSeconds);
                _inputs.Enqueue((_clock, seq, dirX, ClientFrameSeconds));

                ServerCatchUp();

                _predictor.AdvanceRender(ClientFrameSeconds);

                if (_trackRetreat)
                {
                    var retreat = _prevRenderX - _predictor.RenderX;
                    if (retreat > MaxPostStopRetreatUnits)
                    {
                        MaxPostStopRetreatUnits = retreat;
                    }
                }
                _prevRenderX = _predictor.RenderX;

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
                _serverLastInputSeq = input.Seq;
                if (input.DirX != 0d)
                {
                    _serverX += Speed * input.Dt; // moving input integrates.
                    _serverMoving = true;
                }
                else
                {
                    // (0,0) is a stop: Velocity->0, Position untouched. THE FIX: StopMovement bumps StateRevision
                    // ONCE on the moving→stopped transition → mark the own entity unacked so the precise stop pos
                    // re-replicates (and re-includes under loss until acked). A stop on an already-stopped entity
                    // (Velocity already Zero) is a no-op — it does NOT re-mark unacked (no bandwidth at rest).
                    if (_wasMoving)
                    {
                        _ownUnacked = true;
                    }
                    _serverMoving = false;
                }
                _wasMoving = _serverMoving;
            }
        }

        // The snapshot the server emits this tick. The HEADER always rides the fresh LastInputSeq. The PAYLOAD pos:
        //   * experiment: always re-publish the live pos (no delta) → confirmed tracks truth every tick forever.
        //   * migration : force-include the own entity while MOVING (Velocity != 0). At the stop, the transition
        //     revision bump leaves the own entity UNACKED (_ownUnacked), so it is re-included until a snapshot
        //     carrying it is actually DELIVERED — then acked, and at steady rest it is delta'd out (no re-publish).
        // dropStopEdgeSnapshot drops the single snapshot that would carry the final moving / stop-transition pos.
        private void ReconcileFromSnapshot()
        {
            // The server WOULD include the own entity this tick iff: experiment (always), OR it is moving
            // (force-include), OR it is unacked from the stop-transition bump (the fix's self-heal).
            var wouldInclude = ExperimentEveryTickRepublish || _serverMoving || _ownUnacked;

            // Detect a steady-rest re-publish: the entity is stopped, the confirmed base is already at truth, and the
            // server is still trying to include it. With the once-only transition bump this never happens.
            var atSteadyRest = !_serverMoving && !ExperimentEveryTickRepublish
                && System.Math.Abs(_confirmedX - _serverX) <= 1e-9;
            if (wouldInclude && atSteadyRest)
            {
                RepublishesAtSteadyRest++;
            }

            var delivered = wouldInclude;

            // Stop-edge loss: drop the FIRST snapshot that fires after release carrying the precise stop position
            // (the moving tail or the stop-transition re-include). That snapshot is DROPPED — the client gets no
            // fresh pos this tick AND the own entity stays unacked, so the next snapshot re-includes it (the fix).
            if (delivered && _dropStopEdgeSnapshot && _released && !_droppedOnce)
            {
                _droppedOnce = true;
                delivered = false; // dropped on the wire.
            }

            if (delivered)
            {
                _confirmedX = _serverX;
                _ownUnacked = false; // the client received + acked the own entity this snapshot.
            }
            // else: delta'd out / dropped → _confirmedX stays the last re-based value; _ownUnacked persists if set,
            // so the next snapshot re-includes the stop position (the standard unacked-entity self-heal).

            _predictor.Reconcile(new WorldVector(_confirmedX, 0d), _serverLastInputSeq);
        }
    }
}
