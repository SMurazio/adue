using System.Collections.Generic;
using Mmo.Shared.Domain;

namespace Mmo.Client.Core.Continuous;

// CONTINUOUS MIGRATION (Phase 4): the client-side predict -> reconcile -> replay loop for the LOCAL player — a
// near-verbatim port of the proven exp/continuous-movement spike (exp:Mmo.Client.Core.Continuous.ContinuousPredictor),
// with the established Z->Y rename and the SHARED determinism primitives swapped in. Pure and Godot-free so it is
// unit-testable headless; MmoClient owns input/render/transport and calls into this. ONLY the local player; remote
// entities stay raw (Phase 5).
//
// THE MODEL (docs/migration/phase-4-plan.md):
//   * PREDICT: every client RENDER FRAME, integrate the held input locally with the FRAME's dt and IMMEDIATELY
//     (zero latency) advance the predicted position. Per-frame prediction means the predicted advances SMOOTHLY
//     every frame, so the render IS the predicted directly — no sub-tick extrapolation/lead, and a stop settles
//     exactly in place (no snap-back).
//   * BUFFER: each predicted frame is recorded as an unacked input {seq, dirX, dirY, dt}. The seq is monotonic.
//     The buffered dt is the CLAMPED dt the client predicted with (clamped to the SHARED MaxInputDtSeconds — the
//     same clamp the server applies on receive and the send path applies before sending), so buffered == sent ==
//     server-integrated dt under normal play → replay reproduces the server path byte-for-byte.
//   * RECONCILE (on a server (Position, LastInputSeq)): snap a "base" position to the server's authoritative pos,
//     DROP buffered inputs whose seq <= LastInputSeq (the server already integrated them), then REPLAY the rest
//     from that base to recompute the present. If the recomputed present matches the live predicted, nothing
//     visible changes (the prediction was right); a divergence corrects — small errors smooth, large ones snap.
//
// DETERMINISM IS THE WHOLE GAME (docs/migration/phase-4-plan.md "Three determinism gaps"). The predict/replay
// integration MUST reproduce the server's path bit-for-bit: same walls (shared TileWalls.NeighborhoodWallsForMove
// over the SAME blocked set), same radius (replicated on ServerHello), same speed (derived from the quantized
// cadence), same dt (clamped to the shared MaxInputDtSeconds). When those agree, with no packet loss replay lands
// exactly where the live prediction already was, so a slide along a wall opens NO correction (the no-loss ==
// no-correction guarantee, WITH collision).
//
// Reconcile NEVER mutates the buffered inputs' directions or the integrator math — it only re-bases position and
// replays — so the predicted present is, by construction, "server truth + my still-unacked inputs". The rendered
// position is a cosmetic decaying smoothing of that, decoupled so a correction can't feed back and oscillate.
public sealed class ContinuousPredictor
{
    // A render correction larger than this (world units) is snapped instantly instead of smoothed — a teleport /
    // massive desync rather than a normal latency catch-up. PINNED verbatim from the spike. ≫ the Q12.4 snapshot
    // quantization (≤0.0625 u/axis) so quantization NEVER snaps — the offset-decay smooths it invisibly.
    private const double SnapThresholdUnits = 4.0d;

    // Hard cap on buffered unacked inputs. Bounds memory if the server goes silent (we drop the OLDEST, which the
    // server will never ack anyway). Generous so it never clips a legitimate in-flight window. PINNED verbatim.
    private const int MaxBufferedInputs = 256;

    // How fast the visible render OFFSET (predicted minus rendered) decays toward zero (fraction removed per second).
    // The render is `predicted - offset`, so in steady motion the offset is constant (≈0) and the render advances WITH
    // the predicted (zero steady-state lag). The offset is non-zero only right after a reconcile correction; this
    // decays that residual smoothly so the catch-up doesn't pop. PINNED verbatim (14).
    private const double RenderCorrectionPerSecond = 14.0d;

    // The shared per-input dt sanity clamp (== the server's MaxMoveInputDtSeconds == the send path's frame clamp).
    private const double MaxInputDtSeconds = ContinuousMovement.MaxInputDtSeconds;

    // The integrate speed (units/sec) — LIVE-updatable (a MovementSpeedChanged / spawn retune calls SetSpeed) so the
    // predicted integration tracks the server's SpeedUnitsPerSecond. Derived client-side as 1000/EffectiveStepCooldownMs
    // (EXACT at multiplier 1.0; the fractional-multiplier residual is bounded by one tick-quant and absorbed by the
    // reconcile budget — a documented accepted mispredict, not a bug).
    private double _speed;

    // The SHARED blocked-tile set (ZoneModel.BlockedTiles) the predict/replay integration collides against. The SAME
    // set the server derives its walls from, via the SAME TileWalls.NeighborhoodWallsForMove → identical wall set →
    // identical resolved path. Null == open field (no collision). The radius matches the server's replicated radius.
    private readonly IReadOnlySet<TileCoord>? _blocked;
    private readonly double _radius;

    // REUSED scratch for the per-move wall query (no per-frame alloc). TileWalls.NeighborhoodWallsForMove clears it.
    private readonly List<ContinuousCollision.Wall> _wallScratch = new();

    // The predicted (authoritative-consistent) present position: server base + replayed unacked inputs.
    private double _predictedX;
    private double _predictedY;

    // The CORRECTION offset: render = predicted - offset. Zero in steady state (render == predicted, no lag). Set to
    // the pre-correction render error when a reconcile moves the predicted (so the dot stays put at the instant of
    // correction, then the offset decays to zero — a smooth catch-up). DECOUPLED so smoothing can't feed back/ring.
    private double _offsetX;
    private double _offsetY;

    // The last authoritative base we reconciled against (server pos at LastInputSeq). Replay starts here.
    private double _baseX;
    private double _baseY;

    private uint _nextInputSeq;
    private uint _lastAckedSeq;
    private bool _hasReconciled;

    // The ring of unacked inputs, oldest-first. Each is the exact (dir, CLAMPED dt) used to predict one frame, so
    // replay reproduces the predicted path byte-for-byte when no input was lost.
    private readonly List<BufferedInput> _buffer = new();

    private double _lastCorrectionUnits;

    // CONTINUOUS MIGRATION (Phase 4a, re-attach freeze fix): the input seq is a SINGLE persistent monotonic counter
    // owned by MmoClient, not per-predictor-instance — otherwise a mid-session re-attach (F5 prediction toggle,
    // respawn, AOI re-entry) would build a FRESH predictor whose counter restarts at 0, mint 1,2,3… all <= the
    // server's already-high acked cursor N, and have EVERY MoveIntent rejected (inputSeq <= _lastInputSeq) until the
    // local counter climbed back past N — a multi-second freeze proportional to session length. `startInputSeq` seeds
    // the new predictor from the client's high-water so the very next minted seq (++_nextInputSeq) is strictly above
    // every previously-sent seq, hence above the server cursor → always accepted. First spawn passes 0 (cursor N=0).
    public ContinuousPredictor(
        double speed,
        double startX = 0d,
        double startY = 0d,
        IReadOnlySet<TileCoord>? blocked = null,
        double radius = 0d,
        uint startInputSeq = 0u)
    {
        _speed = speed;
        _predictedX = _baseX = startX;
        _predictedY = _baseY = startY;
        _blocked = blocked;
        _radius = radius;
        _nextInputSeq = startInputSeq;
    }

    // The predicted (truth-consistent) present position. Targeting/aim must NOT read this (it reads the confirmed
    // tile); only movement rendering does, via RenderX/RenderY.
    public double PredictedX => _predictedX;
    public double PredictedY => _predictedY;

    // The cosmetic render position (predicted minus the decaying correction offset). What the avatar is drawn at.
    public double RenderX => _predictedX - _offsetX;
    public double RenderY => _predictedY - _offsetY;

    // The latest authoritative base the predictor reconciled against (the server's last reported pos).
    public double ServerX => _baseX;
    public double ServerY => _baseY;

    // Count of unacked inputs still buffered for replay (the in-flight window). Exposed for the readout/tests.
    public int BufferedInputCount => _buffer.Count;

    // The magnitude of the correction the last Reconcile applied to the predicted present (0 == clean match).
    public double LastCorrectionUnits => _lastCorrectionUnits;

    // The current render-vs-predicted offset magnitude (the visible catch-up still in flight). Zero in steady state.
    public double RenderVsPredictedUnits => Math.Sqrt((_offsetX * _offsetX) + (_offsetY * _offsetY));

    // How far ahead the prediction is running vs the last authoritative base (≈ the unacked inputs' worth of motion).
    public double ServerVsPredictedUnits => Distance(_baseX, _baseY, _predictedX, _predictedY);

    // The live integrate speed (units/sec).
    public double Speed => _speed;

    // CONTINUOUS MIGRATION (Phase 4a): the highest input seq this predictor has minted so far (== the last value
    // PredictAndBuffer returned, or the seeded startInputSeq before any mint). MmoClient reads this to keep its
    // persistent high-water counter in sync, so a later re-attach seeds the next predictor strictly above it.
    public uint LastMintedInputSeq => _nextInputSeq;

    // CONTINUOUS MIGRATION (Phase 4): live-update the integrate speed on a MovementSpeedChanged / spawn retune, so the
    // predicted integration keeps tracking the server's SpeedUnitsPerSecond. No re-base — only future frames/replays use
    // the new speed (exactly as the server adopts the new speed on its next input). Guards non-finite / negative.
    public void SetSpeed(double speed)
    {
        if (double.IsFinite(speed) && speed >= 0d)
        {
            _speed = speed;
        }
    }

    // PREDICT + BUFFER one client RENDER FRAME. CLAMPS the frame dt to the shared MaxInputDtSeconds (so the buffered
    // dt == the dt the send path sends == the dt the server integrates), integrates the raw input direction over that
    // clamped dt at the live speed (advancing the predicted present immediately, zero latency), records the input with
    // the CLAMPED dt for replay, and returns the monotonic inputSeq the caller stamps on the MoveIntent it sends this
    // frame. The integrator math is the SAME normalize-then-scale + shared collision the server uses, so server and
    // client agree byte-for-byte (see IntegrateWithCollision).
    public uint PredictAndBuffer(double inputX, double inputY, double dtSeconds)
    {
        var seq = ++_nextInputSeq;

        // Clamp to the shared per-input cap and BUFFER the clamped value — so buffered == sent == server-integrated dt
        // (the dt-alignment linchpin). A non-finite/negative dt collapses to 0 (no motion this frame, like the server).
        var clampedDt = double.IsFinite(dtSeconds) ? Math.Clamp(dtSeconds, 0d, MaxInputDtSeconds) : 0d;

        // Trim defensively from the front if the server has gone silent and the window grew past the cap — dropping the
        // OLDEST unacked input (the server will never ack it once we exceed the window). Keeps the buffer bounded.
        while (_buffer.Count >= MaxBufferedInputs)
        {
            _buffer.RemoveAt(0);
        }

        _buffer.Add(new BufferedInput(seq, inputX, inputY, clampedDt));

        // Integrate this frame from the current predicted position, RESOLVING collision from the running predicted
        // position — exactly as the server applies it from its running authoritative position for the same input.
        (_predictedX, _predictedY) = IntegrateWithCollision(_predictedX, _predictedY, inputX, inputY, clampedDt);

        return seq;
    }

    // RECONCILE against the authoritative (serverPos, lastInputSeq) from a snapshot: snap the base to the server pos,
    // drop acked inputs, replay the rest to recompute the predicted present, and decide the visible correction
    // (smooth vs snap). Deterministic and side-effect-free beyond the predictor's own state.
    public void Reconcile(in WorldVector serverPos, uint lastInputSeq)
    {
        // Monotonic guard: ignore a stale/duplicate state whose LastInputSeq is older than one we already applied
        // (unreliable transport can reorder). Re-basing on an older ack would resurrect trimmed inputs and rubberband.
        if (_hasReconciled && lastInputSeq < _lastAckedSeq)
        {
            return;
        }

        _hasReconciled = true;
        _lastAckedSeq = lastInputSeq;

        // Snap the replay base to the server's authoritative position.
        _baseX = serverPos.X;
        _baseY = serverPos.Y;

        // Drop every buffered input the server has already processed (seq <= LastInputSeq). The remainder are the
        // genuinely-still-unacked inputs to replay forward from the base.
        _buffer.RemoveAll(b => b.Seq <= _lastAckedSeq);

        // REPLAY: recompute the predicted present = base + the unacked inputs' integrated motion. SAME integrator as
        // predict, so with no loss this lands exactly where the live prediction already was.
        var newX = _baseX;
        var newY = _baseY;
        foreach (var input in _buffer)
        {
            (newX, newY) = IntegrateWithCollision(newX, newY, input.DirX, input.DirY, input.Dt);
        }

        // The correction is how far the recomputed present moved from the live predicted present. ~0 with no loss.
        _lastCorrectionUnits = Distance(_predictedX, _predictedY, newX, newY);

        // Capture where the dot is rendered RIGHT NOW (before we move the predicted), so we keep it visually put
        // across the correction and decay the difference smoothly. render_now = predicted_old - offset_old.
        var renderNowX = _predictedX - _offsetX;
        var renderNowY = _predictedY - _offsetY;

        _predictedX = newX;
        _predictedY = newY;

        // New offset = predicted_new - render_now: the dot STAYS at render_now this instant and the offset (which
        // AdvanceRender decays toward zero) carries the visible catch-up. In steady state newX/newY equals the old
        // predicted so the offset is unchanged (≈0) and there is no visible move — no rubberband. On a genuine
        // mispredict the offset jumps to the error and decays smoothly. The offset only ever shrinks → never rings.
        _offsetX = _predictedX - renderNowX;
        _offsetY = _predictedY - renderNowY;

        // Teleport-scale desync: clear the offset so the dot snaps onto the truth rather than smear a long slide.
        if (RenderVsPredictedUnits > SnapThresholdUnits)
        {
            _offsetX = 0d;
            _offsetY = 0d;
        }
    }

    // ADVANCE the cosmetic render catch-up: decay the correction offset toward zero by an exponential step over dt.
    // Called once per render frame. render = predicted - offset, so shrinking the offset slides the dot onto the
    // predicted present. Pure catch-up of the VISIBLE dot; never touches the predicted path, so it cannot oscillate.
    public void AdvanceRender(double dtSeconds)
    {
        if (dtSeconds <= 0d)
        {
            return;
        }

        // Fraction kept this frame: e^(-rate*dt). Frame-rate independent and monotone in [0,1], so the offset always
        // shrinks toward zero and never overshoots.
        var keep = Math.Exp(-RenderCorrectionPerSecond * dtSeconds);
        _offsetX *= keep;
        _offsetY *= keep;
    }

    // The shared integrate+collide step: normalize the raw input direction (so diagonals aren't faster), scale by the
    // live speed over dt to get the desired delta, then RESOLVE that move against the shared walls (circle of _radius
    // vs solid tile AABBs, slide + anti-tunnel) from the given position. The walls are derived per-move from the SAME
    // blocked set via the SAME TileWalls.NeighborhoodWallsForMove the server uses, into the REUSED scratch list, and
    // the same ContinuousCollision.Resolve — IDENTICAL math + collision to the server (Zone.IntegrateMovement), so the
    // authoritative and predicted/replayed paths agree exactly, including AT walls. Null blocked == open-field move.
    private (double X, double Y) IntegrateWithCollision(double x, double y, double inputX, double inputY, double dtSeconds)
    {
        if (dtSeconds <= 0d)
        {
            return (x, y);
        }

        var length = Math.Sqrt((inputX * inputX) + (inputY * inputY));
        if (length <= 1e-6)
        {
            return (x, y);
        }

        var inv = 1d / length;
        var deltaX = inputX * inv * _speed * dtSeconds;
        var deltaY = inputY * inv * _speed * dtSeconds;

        if (_blocked is null || _blocked.Count == 0)
        {
            return (x + deltaX, y + deltaY);
        }

        var start = new WorldVector(x, y);
        var delta = new WorldVector(deltaX, deltaY);
        TileWalls.NeighborhoodWallsForMove(_blocked, start, delta, _radius, _wallScratch);
        return ContinuousCollision.Resolve(x, y, deltaX, deltaY, _radius, _wallScratch);
    }

    private static double Distance(double ax, double ay, double bx, double by)
    {
        var dx = ax - bx;
        var dy = ay - by;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    private readonly record struct BufferedInput(uint Seq, double DirX, double DirY, double Dt);
}
