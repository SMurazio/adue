using System;
using System.Collections.Generic;
using Mmo.Client.Core.Continuous;
using Mmo.Shared.Domain;
using Mmo.Shared.Protocol;
using Xunit;

namespace Mmo.Client.Core.Tests;

// CONTINUOUS MIGRATION (Phase 4) — THE TIMING-FAITHFUL RECONCILE HARNESS (the Phase-4 MUST; replaces the deleted
// UO5/NET2/NET3 movement-netcode guards). The earlier movement misses all passed a headless test the AUTHOR wrote
// because the test inherited the same wrong model that produced the fix. This harness instead models the REAL system
// end-to-end and asserts the live invariants:
//
//   * The CLIENT polls at ~144Hz: each poll it PredictAndBuffers the held input with the frame dt, sends the {seq,
//     dir, dt} to the virtual server, AdvanceRenders, and Reconciles on any snapshot whose latency-delayed delivery
//     time has elapsed.
//   * The SERVER integrates at a REAL 20Hz tick: each tick it drains the inputs whose (latency-delayed) arrival time
//     has elapsed, integrates each through the SHARED ContinuousCollision resolver over the SAME blocked set / radius
//     / derived speed the client uses (advancing LastInputSeq), then emits a snapshot whose Position is QUANTIZED to
//     Q12.4 (PositionEncoding — exactly the wire) and whose LastInputSeq acks the integrated cursor.
//   * Snapshots cross back to the client with LATENCY (+ optional JITTER), and some are DROPPED.
//
// The whole game is determinism: client predict/replay and server integrate run the IDENTICAL shared math on the
// IDENTICAL walls/radius/speed/dt, so with no loss the reconcile opens NO correction — even AT a real wall. The
// invariants below pin that, plus bounded/convergent behaviour under drop/jitter and the sustained-lag dt-budget bite.
public sealed class ContinuousReconcileHarnessTests
{
    // Server tick interval and a representative high client frame rate.
    private const double ServerTickSeconds = 1.0d / 20d;   // 20Hz authoritative integration
    private const double ClientFrameSeconds = 1.0d / 144d; // ~144Hz client poll/predict/render
    private const double Radius = CollisionDefaults.BodyRadius; // 0.5
    // Speed derived as the client does: 1000 / EffectiveStepCooldownMs. 250ms cooldown is tick-aligned at 20Hz, so
    // 1000/250 = 4.0 u/s EXACTLY matches the server's BaseMoveSpeedUnitsPerSecond at multiplier 1.0 (no residual).
    private const double Speed = 1000d / 250d;

    [Fact]
    public void Invariant1_SteadyWalking_NoLoss_ZeroCorrections_WithCollisionAndFixedPoint()
    {
        // No loss, no jitter, modest latency: steady eastward walking through the open field. Because the server
        // integrates the SAME buffered {dir,dt} inputs and the only wire effect is the Q12.4 quantization (≤0.0625
        // u/axis, far under the 4.0 snap threshold), every reconcile must be a clean match — ZERO corrections that
        // exceed the quantization, and NEVER a snap.
        var sim = new Harness(blocked: null, latencyMs: 80, jitterMs: 0, dropEveryNth: 0);
        sim.RunHeld(dirX: 1d, dirY: 0d, seconds: 4d);

        Assert.Equal(0, sim.SnapCount);
        // The largest correction stays within the fixed-point quantization budget (a couple of 1/16-u, with slack for
        // the off-by-one-tick replay ordering under latency). NEVER a real divergence.
        Assert.True(sim.MaxCorrectionUnits <= 0.2d, $"steady walk opened a real correction: {sim.MaxCorrectionUnits}");
    }

    [Fact]
    public void Invariant2_RenderGlidesMonotonic_NeverRetreatsOnStop()
    {
        // Walk east, then STOP (zero input). The rendered X must be monotonic non-decreasing the whole way — it must
        // never retreat when the key is released (the classic over-extrapolation snap-back bug). Tiny epsilon covers
        // float-quantized re-base noise.
        var sim = new Harness(blocked: null, latencyMs: 80, jitterMs: 0, dropEveryNth: 0);
        sim.TrackRenderMonotonicX = true;
        sim.RunHeld(dirX: 1d, dirY: 0d, seconds: 2d);
        sim.RunHeld(dirX: 0d, dirY: 0d, seconds: 2d);

        // Tolerance is well under the 1/16-u (0.0625) wire quantization — a reconcile re-base onto a quantized server
        // position can nudge the render by a sub-quantization sliver, which is NOT the gross over-extrapolation
        // snap-back this guards against (that would be ~a tile). A real snap-back would dwarf this.
        Assert.True(sim.MaxRenderRetreatX <= 0.02d, $"render retreated on the walk/stop: {sim.MaxRenderRetreatX}");
        Assert.Equal(0, sim.SnapCount);
    }

    [Fact]
    public void Invariant3_ReconcilingAgainstARealWall_ZeroCorrections()
    {
        // The Phase-2 payoff vs REAL geometry: drive NE into a wall directly east (blocked tile (10,8)), starting at
        // (8,8). The into-wall (X) component is blocked at the face, the tangential (Y) slides. Because the client and
        // server resolve collision with the IDENTICAL shared resolver / walls / radius, the slide opens NO correction
        // beyond the wire quantization — even though the body is grinding along a wall the whole time.
        var blocked = new HashSet<TileCoord>();
        for (var ty = 0; ty <= 40; ty++) // a tall column at x=10 so the north slide stays blocked for the whole run
        {
            blocked.Add(new TileCoord(10, ty));
        }

        var sim = new Harness(blocked: blocked, latencyMs: 80, jitterMs: 0, dropEveryNth: 0, startX: 8d, startY: 8d);
        sim.RunHeld(dirX: 1d, dirY: 1d, seconds: 4d);

        Assert.Equal(0, sim.SnapCount);
        Assert.True(sim.MaxCorrectionUnits <= 0.2d, $"wall slide opened a real correction: {sim.MaxCorrectionUnits}");
        // It genuinely reached and held the wall (never entered the blocked column: x <= 9.0 = face 9.5 - radius 0.5).
        Assert.True(sim.FinalPredictedX <= 9.0d + 1e-3, $"predicted entered the wall: {sim.FinalPredictedX}");
        Assert.True(sim.FinalPredictedX >= 8.9d, $"never reached the wall to test the slide: {sim.FinalPredictedX}");
    }

    [Fact]
    public void Invariant4_DropAndJitter_BufferBounded_ResyncsConvergesNoOscillation()
    {
        // Heavy drop + jitter: every 3rd snapshot dropped, ±40ms jitter, 120ms latency. The unacked buffer must stay
        // bounded (≤256), the prediction must keep RE-SYNCing as acks arrive, and at the end (input released, all acks
        // delivered) it must CONVERGE onto the server truth with no oscillation (a final clean reconcile that doesn't
        // move anything).
        var sim = new Harness(blocked: null, latencyMs: 120, jitterMs: 40, dropEveryNth: 3);
        sim.RunHeld(dirX: 1d, dirY: 0d, seconds: 5d);
        Assert.True(sim.MaxBufferedInputs <= 256, $"buffer grew unbounded: {sim.MaxBufferedInputs}");

        // Release and let everything drain/deliver: the prediction converges onto the server's authoritative position.
        sim.RunHeld(dirX: 0d, dirY: 0d, seconds: 3d);
        sim.Drain(seconds: 2d);

        var gap = sim.PredictedVsServerTruth();
        Assert.True(gap <= 0.2d, $"did not converge onto server truth at rest: {gap}");

        // A final reconcile against the settled truth must be a clean no-op (no oscillation / runaway).
        var moved = sim.ReconcileOnceAndMeasureCorrection();
        Assert.True(moved <= 0.1d, $"final reconcile oscillated: {moved}");
    }

    [Fact]
    public void Invariant5_SustainedLag_DtBudgetBite_BoundedAndConvergent()
    {
        // The one place predicted != integrated: under SUSTAINED lag the client keeps predicting full-dt frames but the
        // server's wall-clock dt BUDGET caps how much real time it integrates per window, so the server falls behind by
        // a bounded amount. The reconcile must keep the divergence BOUNDED (never runs away) and, once input stops and
        // the budget catches up, CONVERGE. We model the budget bite as the server integrating only a fraction of each
        // input's dt during the lagged window.
        var sim = new Harness(blocked: null, latencyMs: 150, jitterMs: 0, dropEveryNth: 0)
        {
            ServerDtBudgetFactor = 0.6d, // sustained budget pressure: server integrates 60% of requested dt
        };
        sim.RunHeld(dirX: 1d, dirY: 0d, seconds: 5d);

        // Bounded: the prediction leads the server but never explodes, and never snaps repeatedly into a rubberband.
        Assert.True(sim.MaxServerVsPredictedUnits < 50d, $"divergence ran away under sustained lag: {sim.MaxServerVsPredictedUnits}");

        // Release, restore full budget, drain: the server catches up and the prediction converges.
        sim.ServerDtBudgetFactor = 1.0d;
        sim.RunHeld(dirX: 0d, dirY: 0d, seconds: 4d);
        sim.Drain(seconds: 3d);

        var gap = sim.PredictedVsServerTruth();
        Assert.True(gap <= 0.5d, $"did not converge after the dt-budget bite: {gap}");
    }

    // ---- the harness ------------------------------------------------------------------------------------------

    // A faithful client+server+network model. The client predicts/sends at 144Hz; the server integrates at 20Hz with
    // the SHARED resolver and quantizes snapshots to Q12.4; the network applies latency/jitter/drop both ways.
    private sealed class Harness
    {
        private readonly IReadOnlySet<TileCoord>? _blocked;
        private readonly double _latencySeconds;
        private readonly double _jitterSeconds;
        private readonly int _dropEveryNth;
        private readonly ContinuousPredictor _predictor;

        // The virtual server's authoritative state.
        private double _serverX;
        private double _serverY;
        private uint _serverLastInputSeq;
        private readonly List<ContinuousCollision.Wall> _serverScratch = new();

        // In-flight queues (FIFO by release time). Inputs client->server; snapshots server->client.
        private readonly Queue<(double ReleaseAt, uint Seq, double DirX, double DirY, double Dt)> _inputsInFlight = new();
        private readonly Queue<(double ReleaseAt, double PosX, double PosY, uint LastInputSeq)> _snapshotsInFlight = new();

        private double _clock;          // wall-clock seconds
        private double _nextServerTick; // when the next 20Hz tick fires
        private int _snapshotCounter;   // for deterministic drop-every-nth
        private uint _jitterState = 0x9E3779B9u; // deterministic pseudo-jitter (no RNG nondeterminism)

        private double _prevRenderX;
        private bool _haveRenderBaseline;

        public Harness(
            IReadOnlySet<TileCoord>? blocked,
            int latencyMs,
            int jitterMs,
            int dropEveryNth,
            double startX = 0d,
            double startY = 0d)
        {
            _blocked = blocked;
            _latencySeconds = latencyMs / 1000d;
            _jitterSeconds = jitterMs / 1000d;
            _dropEveryNth = dropEveryNth;
            _serverX = startX;
            _serverY = startY;
            _nextServerTick = ServerTickSeconds;
            _predictor = new ContinuousPredictor(Speed, startX, startY, blocked, Radius);
            _prevRenderX = _predictor.RenderX;
        }

        // When < 1.0, the server integrates only this fraction of each input's dt (models the anti-speedhack
        // wall-clock dt-budget biting under sustained lag — the one place predicted != integrated).
        public double ServerDtBudgetFactor { get; set; } = 1.0d;

        public bool TrackRenderMonotonicX { get; set; }

        public int SnapCount { get; private set; }
        public double MaxCorrectionUnits { get; private set; }
        public double MaxRenderRetreatX { get; private set; }
        public int MaxBufferedInputs { get; private set; }
        public double MaxServerVsPredictedUnits { get; private set; }
        public double FinalPredictedX => _predictor.PredictedX;

        // Drive `seconds` of the client holding (dirX,dirY), advancing the client at 144Hz and the server at 20Hz.
        public void RunHeld(double dirX, double dirY, double seconds)
        {
            var end = _clock + seconds;
            while (_clock < end)
            {
                ClientFrame(dirX, dirY);
                ServerCatchUp();
                _clock += ClientFrameSeconds;
            }
        }

        // Drive `seconds` with NO new client input (no prediction/sends) so in-flight snapshots/inputs drain and the
        // client keeps reconciling on delivery. Models "let the network settle".
        public void Drain(double seconds)
        {
            var end = _clock + seconds;
            while (_clock < end)
            {
                DeliverDueSnapshots();
                _predictor.AdvanceRender(ClientFrameSeconds);
                ServerCatchUp();
                _clock += ClientFrameSeconds;
            }
        }

        private void ClientFrame(double dirX, double dirY)
        {
            // Predict + buffer this frame, "send" the input to the server (latency/jitter/drop applied on the client->
            // server leg too, so a lagged input arrives late at the server — faithful to the real one-way delay).
            var seq = _predictor.PredictAndBuffer(dirX, dirY, ClientFrameSeconds);
            EnqueueInput(seq, dirX, dirY, ClientFrameSeconds);

            // Reconcile any snapshot whose delivery time has elapsed, then advance the cosmetic render.
            DeliverDueSnapshots();
            _predictor.AdvanceRender(ClientFrameSeconds);

            MaxBufferedInputs = Math.Max(MaxBufferedInputs, _predictor.BufferedInputCount);
            MaxServerVsPredictedUnits = Math.Max(MaxServerVsPredictedUnits, _predictor.ServerVsPredictedUnits);

            if (TrackRenderMonotonicX)
            {
                if (_haveRenderBaseline)
                {
                    var retreat = _prevRenderX - _predictor.RenderX;
                    if (retreat > MaxRenderRetreatX)
                    {
                        MaxRenderRetreatX = retreat;
                    }
                }

                _prevRenderX = _predictor.RenderX;
                _haveRenderBaseline = true;
            }
        }

        // Fire every server tick whose time has elapsed: drain due inputs, integrate them through the shared resolver,
        // and emit a Q12.4-quantized snapshot back to the client.
        private void ServerCatchUp()
        {
            while (_nextServerTick <= _clock)
            {
                IntegrateDueInputs(_nextServerTick);
                EmitSnapshot(_nextServerTick);
                _nextServerTick += ServerTickSeconds;
            }
        }

        private void IntegrateDueInputs(double upTo)
        {
            while (_inputsInFlight.Count > 0 && _inputsInFlight.Peek().ReleaseAt <= upTo)
            {
                var input = _inputsInFlight.Dequeue();
                _serverLastInputSeq = input.Seq;

                var dt = input.Dt * ServerDtBudgetFactor;
                if (dt <= 0d)
                {
                    continue;
                }

                var len = Math.Sqrt((input.DirX * input.DirX) + (input.DirY * input.DirY));
                if (len <= 1e-6)
                {
                    continue; // a stop input: acks the seq, integrates no motion (matches the server).
                }

                var inv = 1d / len;
                var deltaX = input.DirX * inv * Speed * dt;
                var deltaY = input.DirY * inv * Speed * dt;

                if (_blocked is null || _blocked.Count == 0)
                {
                    _serverX += deltaX;
                    _serverY += deltaY;
                }
                else
                {
                    var start = new WorldVector(_serverX, _serverY);
                    var delta = new WorldVector(deltaX, deltaY);
                    TileWalls.NeighborhoodWallsForMove(_blocked, start, delta, Radius, _serverScratch);
                    (_serverX, _serverY) = ContinuousCollision.Resolve(_serverX, _serverY, deltaX, deltaY, Radius, _serverScratch);
                }
            }
        }

        // Emit a snapshot: quantize the authoritative position to Q12.4 (exactly the wire), with the integrated cursor.
        private void EmitSnapshot(double atTime)
        {
            _snapshotCounter++;
            if (_dropEveryNth > 0 && _snapshotCounter % _dropEveryNth == 0)
            {
                return; // dropped in flight.
            }

            var (qx, qy) = PositionEncoding.Encode(new WorldVector(_serverX, _serverY));
            var quantized = PositionEncoding.Decode(qx, qy);
            var releaseAt = atTime + _latencySeconds + NextJitter();
            _snapshotsInFlight.Enqueue((releaseAt, quantized.X, quantized.Y, _serverLastInputSeq));
        }

        private void DeliverDueSnapshots()
        {
            // Snapshots can be reordered by jitter; the predictor's monotonic LastInputSeq guard handles a stale one.
            while (_snapshotsInFlight.Count > 0 && _snapshotsInFlight.Peek().ReleaseAt <= _clock)
            {
                var snap = _snapshotsInFlight.Dequeue();
                _predictor.Reconcile(new WorldVector(snap.PosX, snap.PosY), snap.LastInputSeq);
                var correction = _predictor.LastCorrectionUnits;
                if (correction > MaxCorrectionUnits)
                {
                    MaxCorrectionUnits = correction;
                }

                if (_predictor.RenderVsPredictedUnits > 4.0d || correction > 4.0d)
                {
                    SnapCount++;
                }
            }
        }

        private void EnqueueInput(uint seq, double dirX, double dirY, double dt)
        {
            // The client->server leg gets the same one-way latency (+jitter); inputs are NOT dropped (the per-frame
            // model is self-redundant on the wire, and dropping the ack — the snapshot — is the loss case under test).
            var releaseAt = _clock + _latencySeconds + NextJitter();
            _inputsInFlight.Enqueue((releaseAt, seq, dirX, dirY, dt));
        }

        // Deterministic bounded jitter in [-_jitterSeconds, +_jitterSeconds] (xorshift — no RNG nondeterminism).
        private double NextJitter()
        {
            if (_jitterSeconds <= 0d)
            {
                return 0d;
            }

            _jitterState ^= _jitterState << 13;
            _jitterState ^= _jitterState >> 17;
            _jitterState ^= _jitterState << 5;
            var unit = (_jitterState & 0xFFFFFF) / (double)0xFFFFFF; // [0,1]
            return ((unit * 2d) - 1d) * _jitterSeconds;
        }

        // The gap between the predicted present and the server's authoritative truth (after everything drained).
        public double PredictedVsServerTruth()
        {
            var dx = _predictor.PredictedX - _serverX;
            var dy = _predictor.PredictedY - _serverY;
            return Math.Sqrt((dx * dx) + (dy * dy));
        }

        // Reconcile once against the current settled server truth and return how far the predicted moved (oscillation
        // probe — should be ~0 once converged).
        public double ReconcileOnceAndMeasureCorrection()
        {
            var (qx, qy) = PositionEncoding.Encode(new WorldVector(_serverX, _serverY));
            var quantized = PositionEncoding.Decode(qx, qy);
            _predictor.Reconcile(quantized, _serverLastInputSeq);
            return _predictor.LastCorrectionUnits;
        }
    }
}
