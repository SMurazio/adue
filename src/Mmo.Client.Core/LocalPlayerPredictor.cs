using Mmo.Shared.Domain;

namespace Mmo.Client.Core;

// Client-side movement prediction for the LOCAL player only (S53 redo, UO/ClassicUO-style). Mirrors the
// server's held-intent step loop (Mmo.Server.Runtime.WorldEntity.TryStep + GameServer.StepHeldMovementIntents)
// so the local avatar moves almost the instant the player inputs, instead of waiting a round-trip for the
// server to confirm each tile. Remote entities are untouched — they stay pure interpolation. The server
// remains fully authoritative; this is a client-side guess that snaps to the truth on the rare divergence.
//
// S81 — TICK-GRID MIRROR (the parity fix proven in S80). The server acts ONLY on its integer tick grid: every
// turn/step is decided at a tick boundary, sampling the held direction at that instant. The pre-S81 predictor
// free-ran its step/turn schedule on CONTINUOUS wall-clock ms, so on a turn the two sides sampled the rapidly
// changing held direction at DIFFERENT instants and made different turn-vs-step decisions — diverging by a
// whole tile AND a step-seq every turn (the spam-left-right gap). The fix makes the predictor a faithful mirror
// of the server's tick grid: it maps wall-clock → serverTick via a snapshot-anchored calibration and runs the
// EXACT same integer `_nextEligibleTick` gate, processing each new tick boundary once, in order, sampling the
// held direction at that boundary just like the server. The cost (option A, decided in S80): the first step on
// a fresh idle→move waits for the next tick boundary (≤ one tick, ~25 ms avg) instead of firing the same
// instant — in exchange the avatar is pixel-exact with the server through arbitrary turns at 0 latency.
//
// Why this is a REDO (attempt 1): it rendered the predicted local player THROUGH the buffered TileInterpolator,
// whose job is to render confirmed state ~150 ms IN THE PAST for jitter smoothing. Pushing "where I am NOW"
// through a "show the past" buffer cancelled the snappiness, and feeding it backward correction tiles made it
// oscillate — the rubber-band. The fix: the predictor renders the local player AT the predicted tile with its
// OWN present-time step-tween (old tile center -> new tile center over the step duration), with NO playout
// delay. Reconcile is a plain UO resync: on divergence, retarget the tween at the server's truth (a tiny
// present-time blend for a near miss, an instant snap for a large jump); never queue backward tiles.
//
// S77/S83 — server-authoritative reconcile by step-sequence (re-anchor + re-project in-flight). Every accepted
// step bumps PredictedStepSeq in lockstep with the server's WorldEntity.StepSequence (S76), and the snapshot
// carries the recipient's authoritative step-sequence (RecipientStepSeq). S77 matched a confirm to a predicted
// step in a recorded history ring and only re-anchored on a tile MISMATCH, otherwise touching nothing; its
// while-moving convergence was gated on !_moving. S83 found the root cause of the rapid-turn desync: the
// predictor flips its held direction INSTANTLY on input but the server sees it ONE+ tick later, so at a turn the
// two sides feed the same-numbered tick different inputs and the prediction drifts a tile per turn — and the
// S77 model could not pull a while-moving prediction back (the match path touched nothing; the mismatch replay
// re-ran the SAME diverged history; only !_moving converged). S83 replaces that with the textbook resync: on
// EVERY confirming snapshot re-anchor _predictedTile/_predictedStepSeq to the confirmed tile, then re-project
// ONLY the genuinely in-flight steps (serverStepSeq+1 .. PredictedStepSeq) forward in the CURRENTLY HELD
// direction. This caps divergence to the bounded in-flight count at ALL times (spam can't ratchet) and converges
// EXACTLY to the server when input pauses (the in-flight count drains to 0). See Reconcile.
//
// Faithful mirror of the server rule (now on the tick grid):
//   * each tick boundary, while the held intent is Moving and the entity is eligible (serverTick >=
//     _nextEligibleTick), it resolves ONE step exactly like WorldEntity.TryStep: it sets facing to the held
//     direction (S98 — a direction change steps immediately, no separate turn beat) and MOVES one tile iff
//     walkable (same IsWalkable / no-corner-cutting rule, +stepCooldownTicks and bump PredictedStepSeq); a
//     blocked target holds at the wall without consuming the cooldown.
//   * the gate is timed in INTEGER server ticks (the wall clock is mapped to serverTick by calibration), so the
//     predictor samples the held direction at the SAME tick boundaries the server does and never out-steps it.
//
// This class is pure and deterministic (no clock of its own, no network), so it unit-tests by feeding a held
// intent + a wall clock (mapped to ticks) + confirmed tiles and asserting the predicted tile sequence + reconcile
// outcomes. Calibration defaults to (serverTickRef 0 @ wall 0), so a test that ticks at `tick * tickMs` drives
// integer ticks exactly; the real client re-anchors it from snapshot.ServerTick (CalibrateToServerTick).
public sealed class LocalPlayerPredictor
{
    // A correction larger than this (in tiles, Chebyshev) is treated as a teleport/knockback and snaps the
    // render instantly instead of tweening — anything within is a normal start/stop or single-step
    // disagreement and a short present-time blend. One tile covers the worst-case steady-state in-flight
    // divergence; a couple of tiles of slack absorbs a brief multi-step lag spike without snapping. Above
    // that, a visible jump is unavoidable, so snap cleanly rather than smear a long slide. S83: this only
    // decides HOW a correction is shown (blend vs snap) once the re-anchor+re-project has computed the present
    // tile — whether the present tile moved at all (Matched vs Corrected) is decided by the re-projection.
    private const int SnapCorrectionThresholdTiles = 3;

    // Cap the catch-up so a huge clock gap (e.g. a long stall / breakpoint) can't spin a pathological loop; the
    // next snapshot re-bases anyway. 8 mirrors the interpolator's per-sample step cap. Applied to the number of
    // tick boundaries processed per Tick call.
    private const int MaxTicksPerCall = 8;

    // The maximum number of in-flight (sent-but-unacked) predicted steps Reconcile will re-project forward from
    // the re-anchored confirmed tile. The genuine un-acked lead on a sane uplink is ~1 step; a larger
    // predictedStepSeq - serverStepSeq is the input-arrival-skew over-prediction (the predictor's instantaneous
    // input out-stepping the server's delayed input), which must NOT be re-projected — it is corrected toward the
    // server. This caps the predicted head's lead over the confirmed tile at all times (no ratchet), while still
    // letting normal prediction lead by the genuine snapshot lag. 2 absorbs a brief two-snapshot lag without
    // clamping legitimate prediction.
    private const int MaxInFlightLead = 2;

    // Bounded ring of the DIRECTION of each accepted step keyed by its step-seq, kept only so Reconcile can
    // re-project the genuinely in-flight steps (serverStepSeq+1 .. PredictedStepSeq) from the re-anchored
    // confirmed tile along the path the prediction ACTUALLY took (not the latest held direction, which would
    // transiently mis-place the head during a turn). 32 comfortably covers the worst realistic in-flight lead
    // (a round-trip's worth of unconfirmed steps); a longer-in-flight step falls back to the held direction.
    private const int InFlightDirCapacity = 32;
    private readonly Direction8[] _inFlightDir = new Direction8[InFlightDirCapacity];

    private readonly Func<TileCoord, bool> _isWalkable;

    private TileCoord _predictedTile;
    // S77: the predictor's count of ACCEPTED tile moves, the exact mirror of the server's
    // WorldEntity.StepSequence (S76) — bumped ONLY on an accepted step (AdvanceOneStep), never on a turn or a
    // blocked/cooldown step. Reconcile uses it (vs the snapshot's RecipientStepSeq) to size the in-flight steps.
    private uint _predictedStepSeq;
    // S77: the highest serverStepSeq ever fed to Reconcile. A defensive monotonic guard: the client already
    // drops out-of-order snapshots (MmoClient.HandleSnapshot rejects any SnapshotSequence <= the last applied,
    // and RecipientStepSeq is the server's monotonically non-decreasing count of our accepted moves, so a
    // reordered older snapshot never reaches Reconcile), but a reordered confirm carrying a LOWER serverStepSeq
    // would otherwise re-anchor onto a stale tile. We ignore any Reconcile whose seq is older than one we
    // already processed.
    private uint _highestReconciledStepSeq;
    private bool _hasReconciled;
    // RESYNC1: the last server-confirmed tile Reconcile re-anchored on (the authoritative position at
    // _highestReconciledStepSeq). Remembered ONLY so the manual ForceResync primitive has an authoritative target
    // to snap the prediction onto — Reconcile already re-anchors _predictedTile to this on every confirm, but the
    // value is then re-projected forward by the in-flight steps, so the bare confirmed tile is not otherwise
    // retained. Stored at the top of Reconcile before any re-projection; changes nothing about the reconcile
    // behaviour (write-only here, read only by ForceResync). Defaults to the initial tile until the first confirm.
    private TileCoord _lastConfirmedTile;

    // DIAG1: reconcile-outcome tallies (see the public accessors). Bumped once per Reconcile call at each return
    // point; reset by ResetReconcileCounters so the human can zero them before a loss burst. Measurement only.
    private uint _reconcileMatched;
    private uint _reconcileCorrected;
    private uint _reconcileSnapped;

    private Direction8 _facing;
    private bool _moving;
    private Direction8 _direction;

    // UO3: client-driven (UoClientDriven) mode flag. In this mode EVERY accepted predicted step is emitted as an
    // explicit StepCommitRequest the server FOLLOWS (the held-intent pacer is off for the session), so each
    // predicted-but-unconfirmed step is a GENUINE banked, in-flight commit — not a guess the predictor made that
    // the server might never take. That changes Reconcile's at-rest behaviour: on release (!_moving) the
    // predicted-past-the-confirm steps are the RTT-worth of commits the server is still working through, so the
    // render must HOLD at the banked head and settle FORWARD as the confirms arrive, NOT collapse DOWN onto the
    // (RTT-behind) confirmed tile — the backward-snap-on-release bug. Default false keeps model A / the parity
    // tests / the cosmetic modes byte-for-byte (those have no banked commit stream; their over-prediction past a
    // release IS overshoot the stopped server will never confirm, so they still converge down).
    private bool _clientDriven;

    // UO4 — stop-on-reversal (settle-then-go). When on, a ~180° flip of the held direction while moving inserts one
    // clean settle beat before the avatar resumes the new way, instead of reversing mid-step (the left-right
    // bounce). _stopOnReversal is the live toggle (default OFF = today's immediate reverse). _lastAcceptedDir is the
    // direction of the most recent ACCEPTED step (the avatar's current travel direction; only valid once
    // _hasAcceptedStep is set). _settleReversalArmed is set by SetIntent when a 180° flip is detected while moving,
    // and consumed by Tick at the next eligible boundary as a no-step/no-commit settle beat.
    private bool _stopOnReversal;
    private Direction8 _lastAcceptedDir;
    private bool _hasAcceptedStep;
    private bool _settleReversalArmed;

    // ---- Tick grid (S81) -------------------------------------------------------------------------------
    // The server's tick interval in ms; the unit of the whole gate. cadence/turn-delay are expressed as an
    // INTEGER number of these ticks so the predictor steps exactly where WorldEntity.TryStep does.
    private double _tickMs;
    // Effective per-step cooldown in WHOLE ticks, the exact mirror of the server's stepCooldownTicks. Derived
    // from the ms cadence (already tick-quantised on the wire) by rounding to the nearest tick; always >= 1 (a
    // step always costs at least a beat).
    private uint _stepCooldownTicks;
    // Wall-clock → serverTick calibration: serverTick(now) = _serverTickRef + floor((nowMs - _wallRefMs)/tickMs).
    // Anchored to a snapshot's ServerTick at its arrival wall-time (CalibrateToServerTick); defaults to
    // (0 @ wall 0) so a test driving `now = tick * tickMs` yields integer tick == that tick exactly.
    private long _serverTickRef;
    private double _wallRefMs;
    // True once the first snapshot has anchored the frame onto the server's true tick. Until then the frame is
    // the default (0 @ wall 0) so deterministic tests that drive `now = tick * tickMs` get integer ticks.
    private bool _hasCalibrated;
    // Smoothing memory: the last serverTick we estimated. The estimate is clamped monotonic non-decreasing so
    // snapshot-arrival jitter (NTP-free wall clock) can never rewind the tick the gate runs on — a rewind would
    // re-process a tick boundary and double-step. The calibration nudge (CalibrateToServerTick) is likewise
    // clamped to at most one tick of correction per snapshot so a late/early snapshot doesn't jump the estimate.
    private long _lastEstimatedTick;
    private bool _hasEstimatedTick;
    // The earliest server tick at which the next step may fire — the exact mirror of
    // WorldEntity._nextEligibleTick. Null until the first step is armed (SetIntent on idle->move quantises it to
    // the next tick with ceil). An accepted step sets it to actionTick + stepCooldownTicks; a blocked step
    // advances it by one tick. SURVIVES a stop so a quick stop->start respects the cooldown already consumed and
    // never double-steps. (S98: turn-then-move removed — a direction change steps immediately, facing on the
    // step; there is no separate turn beat or turn delay.)
    private long? _nextEligibleTick;

    private double _cadenceMs;

    // ---- Present-time render tween (the snappy part; NOT a playout buffer) ------------------------------
    // The local player is rendered by sampling THIS tween at the current wall-clock time — old tile center ->
    // new tile center over the step duration, started the instant the step is accepted (zero delay). On
    // reconcile divergence the tween is retargeted at the server's truth (blend) or hard-set (snap).
    private RenderPosition _renderFrom;
    private RenderPosition _renderTo;
    private TimeSpan _tweenStartedAt;
    private double _tweenDurationMs;
    private RenderPosition _renderPosition;

    // tickMs defaults to 50 (the server's 20 Hz tick). The client passes the ServerHello-advertised,
    // tick-quantised cadence and the real tick interval so prediction stays in lockstep.
    public LocalPlayerPredictor(
        TileCoord initialTile,
        Direction8 facing,
        double cadenceMs,
        Func<TileCoord, bool> isWalkable,
        double tickMs = 50d)
    {
        _isWalkable = isWalkable ?? throw new ArgumentNullException(nameof(isWalkable));
        _predictedTile = initialTile;
        _lastConfirmedTile = initialTile;
        _facing = facing;
        _tickMs = Math.Max(1, tickMs);
        _cadenceMs = Math.Max(1, cadenceMs);
        RecomputeTickCounts();
        var at = RenderPosition.FromTile(initialTile);
        _renderFrom = at;
        _renderTo = at;
        _renderPosition = at;
        _tweenDurationMs = _cadenceMs;
    }

    // The tile the predictor currently believes the local player occupies. This is the predicted (snappy)
    // position, ahead of the server's last confirmation by the in-flight steps. Harvest/interact targeting
    // must NOT use this (see the design note) — it is for movement rendering only.
    public TileCoord PredictedTile => _predictedTile;

    // S77: the predictor's count of accepted tile moves, the exact mirror of the server's StepSequence (S76).
    // Exposed read-only so the parity test can assert it tracks the server tick-for-tick.
    public uint PredictedStepSeq => _predictedStepSeq;

    // DIAG1: live reconcile-outcome counters since the last ResetReconcileCounters(). Pure read-outs — they are
    // bumped at the single return points of Reconcile (one per call) and change NOTHING about the reconcile
    // behaviour. They let the F3 readout show how the prediction is being corrected under loss: a healthy stream
    // is mostly Matched (the prediction was valid and ahead, no render move); Corrected/Snapped climb when the
    // server's confirm diverges from the prediction (a re-base pulled the head back). Used with pred/conf/lead to
    // separate "the lead drains via benign Matched confirms" from "the lead is being forcibly Corrected/Snapped".
    public uint ReconcileMatched => _reconcileMatched;
    public uint ReconcileCorrected => _reconcileCorrected;
    public uint ReconcileSnapped => _reconcileSnapped;

    // DIAG1: the last serverStepSeq Reconcile re-anchored on — the server's count of OUR accepted tile moves that
    // the client has LEARNED (the snapshot's RecipientStepSeq). Exposed so the readout can show `conf` directly
    // from the predictor (the same value the re-base used), and `lead` = PredictedStepSeq - this.
    public uint LastReconciledStepSeq => _hasReconciled ? _highestReconciledStepSeq : 0u;

    public Direction8 Facing => _facing;

    public bool IsMoving => _moving;

    // NET3: the predictor's best estimate of the CURRENT integer server tick at wall-clock `now` (the same
    // monotonic-clamped mapping the step gate uses). Used to stamp an authored tick on a commit that does NOT come
    // from the step loop — model B's release "finish this step now" commit, which is authored at the present tick
    // rather than a banked gate boundary. The UoClientDriven per-step stream uses the gate tick from Tick instead.
    public long EstimateServerTick(TimeSpan now) => EstimateTick(now.TotalMilliseconds);

    public double CadenceMs => _cadenceMs;

    public double TickMs => _tickMs;

    // The present-time render position for the local player: where the avatar is shown RIGHT NOW. Advanced by
    // Tick (per accepted step) and Reconcile (retarget on divergence); read via Sample(now).
    public RenderPosition RenderPosition => _renderPosition;

    // Adopts a new step cadence immediately (S51 MovementSpeedChanged / EntitySpawn). Re-derives the tick-count
    // cooldown so subsequent steps use the new cadence; the already-armed _nextEligibleTick is unchanged.
    // Predicting at the current cadence and switching on the wire message mirrors the server applying the new
    // EffectiveStepCooldown.
    public void SetCadence(double cadenceMs)
    {
        _cadenceMs = Math.Max(1, cadenceMs);
        RecomputeTickCounts();
    }

    // S81: adopts the server's tick interval (1000 / TickRate). Re-derives the integer tick counts so the gate
    // mirrors the server's stepCooldownTicks exactly. The client sets this from ServerHello.
    public void SetTickMs(double tickMs)
    {
        _tickMs = Math.Max(1, tickMs);
        RecomputeTickCounts();
    }

    // UO3: declares whether this predictor is driving the UoClientDriven mode (per-step commits the server
    // follows). The client flips it when entering/leaving UoClientDriven (SetMovementRenderMode / EnsurePredictor),
    // before/in step with the MovementModeMessage that flips the server's pacing. Only Reconcile reads it (its
    // at-rest hold-for-banked-commits behaviour); stepping, calibration, and the cosmetic/Predicted paths are
    // untouched. Default false (server-paced) keeps every existing mode byte-for-byte.
    public void SetClientDriven(bool clientDriven)
    {
        _clientDriven = clientDriven;
    }

    // UO3: read-only view of the client-driven flag (for the F6 panel / tests).
    public bool ClientDriven => _clientDriven;

    // UO4: live-toggles the "stop on reversal" (settle-then-go) behaviour. When a held direction flips ~180° to the
    // OPPOSITE of the direction the avatar is currently travelling, the predictor inserts ONE clean settle beat
    // (hold on the current tile, no step, no commit) instead of stepping the reversal mid-tween — then resumes
    // stepping in the new direction from that settled tile. Latency-free: it only suppresses the reverse step; it
    // adds no input delay to any non-180° change. Default OFF (the existing immediate-reverse behaviour) so it can
    // be A/B'd. See SetIntent (detection) and Tick (the settle beat).
    public void SetStopOnReversal(bool enabled)
    {
        _stopOnReversal = enabled;
    }

    // UO4: read-only view of the stop-on-reversal flag (for the F6 panel / tests).
    public bool StopOnReversal => _stopOnReversal;

    // S81: re-anchors the wall-clock → serverTick calibration on a snapshot whose authoritative tick is known.
    // serverTick is the tick the snapshot was produced at; receivedAt is when it arrived (wall clock). We want
    // serverTick(receivedAt) to read serverTick. A RAW re-base (set ref = serverTick @ receivedAt) would jump on
    // every snapshot because the NTP-free wall clock and the variable snapshot arrival jitter; instead we nudge
    // the estimate toward the truth by AT MOST one tick per snapshot and never let it rewind, so the tick the
    // gate runs on advances smoothly and monotonically (the #1 real-client risk flagged in S80). The first
    // calibration seeds the reference exactly.
    public void CalibrateToServerTick(long serverTick, TimeSpan receivedAt)
    {
        var receivedMs = receivedAt.TotalMilliseconds;
        if (!_hasCalibrated)
        {
            // First snapshot: re-seed the (until-now default 0 @ wall 0) frame onto the server's true tick. If
            // an action was already armed in the default frame (a press landed before the first snapshot — rare,
            // since the predictor attaches only once the entity exists), shift it by the same frame delta so it
            // stays a valid server-tick target instead of becoming stale (which would fire a catch-up burst).
            var frameDelta = serverTick - EstimateRawTick(receivedMs);
            _serverTickRef = serverTick;
            _wallRefMs = receivedMs;
            _hasCalibrated = true;
            if (_nextEligibleTick.HasValue)
            {
                _nextEligibleTick += frameDelta;
            }

            // Seed the monotonic floor at the freshly-anchored estimate.
            _lastEstimatedTick = serverTick;
            _hasEstimatedTick = true;
            return;
        }

        // What the current calibration estimates the server tick to be at this snapshot's arrival, vs the truth.
        var estimated = EstimateRawTick(receivedMs);
        var error = serverTick - estimated;
        // Clamp the correction to ±1 tick so jitter can't jump the grid; bias the reference by that clamped
        // amount (shifting _serverTickRef shifts every future estimate by the same integer). The monotonic
        // floor moves by the SAME correction so it stays in the (now re-aligned) server-tick frame and neither
        // fights a downward correction nor leaves the gate stranded after an upward one. _nextEligibleTick is a
        // genuine server-tick target and stays valid in the corrected frame, so it is left untouched.
        var correction = Math.Clamp(error, -1, 1);
        _serverTickRef += correction;
        if (_hasEstimatedTick)
        {
            _lastEstimatedTick += correction;
        }
    }

    // Records the held movement intent (the same state the client sends as a MoveIntent). The step schedule
    // mirrors the server's gate EXACTLY (Mmo.Server.Runtime.WorldEntity.TryStep): the next step is due at the
    // integer _nextEligibleTick (stepCooldownTicks after the last accepted step), and a direction change updates
    // the held direction but does NOT bring the next step earlier (S98: it just steps in the new direction).
    // So:
    //   * A fresh start from idle is quantised to the NEXT tick boundary (S81): the server received the intent
    //     at the press and can only act at the smallest tick >= press, so we arm the first action at
    //     ceil((pressMs - wallRef)/tickMs). `ceil` (not floor+1) is exact on a boundary press. This costs up to
    //     one tick of first-step latency vs the pre-S81 immediate fire, in exchange for exact tile/seq parity.
    //   * A quick stop->start does NOT double-step: keyup leaves _nextEligibleTick intact, so a re-press keeps
    //     the already-computed eligible tick (the server's _nextEligibleTick survives the stop the same way).
    //   * Rapid direction flips while moving keep the running schedule untouched, so the prediction resolves the
    //     SAME action at the SAME tick the server does instead of out-stepping it and snapping back.
    // On keyup (moving=false) forward projection stops at once and the avatar holds at the predicted tile
    // (the in-flight tween finishes) until the server's confirmed stop lands.
    public void SetIntent(bool moving, Direction8 direction, TimeSpan now)
    {
        if (moving)
        {
            // UO4 stop-on-reversal: detect a ~180° flip of the held direction AGAINST the avatar's current travel
            // direction (the last accepted step), while already moving, BEFORE we overwrite _direction. Arm a one-
            // beat settle so Tick holds on the current tile for one cooldown and then resumes the new way, instead
            // of stepping the reverse mid-tween. Only when the toggle is on, we're already moving, at least one step
            // has been taken, and the flip is the exact opposite (a non-180° change is unaffected — it steps
            // immediately per S98). Re-arm is idempotent: a repeated opposite intent inside the same beat keeps a
            // single settle armed.
            if (_stopOnReversal && _moving && _hasAcceptedStep && IsOpposite(direction, _lastAcceptedDir))
            {
                _settleReversalArmed = true;
            }

            // Arm the first action only on a true idle->moving transition. Quantise the press to the next tick
            // boundary with ceil; if a surviving _nextEligibleTick from a prior action is LATER (a quick
            // stop->start), keep it so the cooldown/turn-delay already consumed is respected.
            if (!_moving)
            {
                var pressTick = CeilTick(now.TotalMilliseconds);
                _nextEligibleTick = _nextEligibleTick.HasValue
                    ? Math.Max(_nextEligibleTick.Value, pressTick)
                    : pressTick;
            }

            _moving = true;
            _direction = direction;
            // Do NOT set _facing here. The server changes Facing only inside TryStep at a step boundary (S98: the
            // step itself faces you), never at intent-receive; Tick mirrors that. _facing is updated when a step
            // is resolved (AdvanceOneStep / the blocked-hold branch in Tick).
        }
        else
        {
            _moving = false;
            // UO4: a release cancels any pending reversal settle — the avatar is stopping anyway, so there is no
            // reverse step to suppress.
            _settleReversalArmed = false;
        }
    }

    // UO4: two directions are ~180° opposite iff their tile deltas are exact negations (E<->W, N<->S, NE<->SW,
    // NW<->SE). Equivalent to (a - b) mod 8 == 4 on the Direction8 ring; the delta-negation form is used so it
    // stays correct if the enum order ever changes.
    private static bool IsOpposite(Direction8 a, Direction8 b)
    {
        var da = a.Delta();
        var db = b.Delta();
        return da.X == -db.X && da.Y == -db.Y;
    }

    // Advances the prediction to wall-clock time now: processes each elapsed tick boundary once, in order,
    // resolving one action per boundary (turn / step / blocked-hold) exactly like the server, while the held
    // intent is moving. Idempotent within a tick window: calling it every frame only acts when a new tick
    // boundary has elapsed. Returns true if the predicted tile changed this call. Always samples the render
    // tween forward to now so the avatar glides smoothly between steps.
    public bool Tick(TimeSpan now)
    {
        return Tick(now, default, out _);
    }

    // UO1 overload: same as Tick(now) but ALSO reports the direction of each step ACCEPTED this call into the
    // caller-supplied buffer, in order, so the client-driven render mode can emit one StepCommitRequest per
    // accepted step. The multi-step catch-up loop (a single Tick can resolve up to MaxTicksPerCall accepted steps
    // when frames lag) fills the buffer per accept; blocked/cooldown ticks are NOT reported (the server only
    // advances on an accepted move, so only accepts get a commit). acceptedCount is the number of entries written;
    // entries beyond the buffer's length are dropped from the report (never from the prediction — the tile still
    // advances) so a too-small buffer can only under-report, never corrupt state. Pass an empty span to ignore the
    // report (what the parameterless Tick does). Returns true iff the predicted tile changed this call.
    public bool Tick(TimeSpan now, Span<Direction8> acceptedSteps, out int acceptedCount)
    {
        return Tick(now, acceptedSteps, default, out acceptedCount);
    }

    // NET3 overload: same as Tick(now, acceptedSteps, ...) but ALSO reports the AUTHORED tick of each accepted step
    // — the exact integer server tick the predictor's gate fired the step on (actionTick = _nextEligibleTick at the
    // boundary it resolved). This is the SAME tick the prediction advanced on, so a commit stamped with it lets the
    // server replay the step at its authored time (NET3 authored-tick application) rather than the receive tick —
    // killing the bundled-recovered-commit cooldown rejection (the loss desync). CRITICAL: it MUST be this gate tick,
    // not a separately-sampled clock, or the server's authored-tick application won't match the prediction (the
    // clock-mismatch snapping lesson). acceptedTicks is filled in lockstep with acceptedSteps (same index, same
    // under-report-never-corrupt rule); pass an empty span to ignore the tick report. The render/stepping behaviour
    // is byte-for-byte identical to the dir-only overload.
    public bool Tick(TimeSpan now, Span<Direction8> acceptedSteps, Span<long> acceptedTicks, out int acceptedCount)
    {
        acceptedCount = 0;
        var changed = false;
        if (_moving && _nextEligibleTick.HasValue)
        {
            var currentTick = EstimateTick(now.TotalMilliseconds);
            // Resolve one ACTION per eligible tick boundary that has elapsed, in order, exactly like the server
            // firing TryStep on each tick its entity is eligible. We jump straight to _nextEligibleTick (the
            // idle boundaries between actions resolve nothing, so there is no need to walk them one by one — and
            // walking them would burn the catch-up cap on empty ticks). The cap bounds the number of ACTIONS so
            // a huge clock gap can't spin a pathological loop; the next snapshot re-bases anyway.
            for (var processed = 0; processed < MaxTicksPerCall && _nextEligibleTick.Value <= currentTick; processed++)
            {
                var actionTick = _nextEligibleTick.Value;

                // UO4 settle-then-go: a ~180° reversal was detected while moving (SetIntent armed it). Spend THIS
                // eligible boundary as a clean settle instead of stepping the reverse: do NOT move the tile and do
                // NOT emit a commit (so the server, which follows commits in UO mode, stays in lockstep), just turn
                // to face the new direction and consume one full cooldown. The avatar's in-flight tween finishes
                // onto its current tile (a clean stop), then the NEXT boundary steps the new direction normally —
                // one clean settle instead of a mid-step bounce. Only ever fires when the toggle is on.
                if (_settleReversalArmed)
                {
                    _settleReversalArmed = false;
                    _facing = _direction;
                    _nextEligibleTick = actionTick + _stepCooldownTicks;
                    continue;
                }

                // S98: a direction change steps IMMEDIATELY in the new direction — there is no separate turn
                // beat. The step itself faces you (AdvanceOneStep / the blocked-hold branch below set _facing =
                // _direction), exactly mirroring the server's WorldEntity.TryStep after S98.
                var delta = _direction.Delta();
                var target = _predictedTile.Offset(delta.X, delta.Y);
                if (!IsStepWalkable(delta, target))
                {
                    // Blocked: hold at the wall. The cooldown is NOT consumed (the server only advances its
                    // step tick on an accepted move), so we keep facing and re-test the NEXT tick — advance
                    // _nextEligibleTick by one tick so the loop makes progress without consuming the cooldown.
                    _facing = _direction;
                    _nextEligibleTick = actionTick + 1;
                    continue;
                }

                // Accepted step: advance the tile + step-seq (recording its direction for in-flight re-projection),
                // and start the present-time tween from
                // where we are showing NOW (carries any in-flight position so back-to-back steps glide
                // continuously) toward the new tile center, over one cadence, beginning at the boundary's
                // wall-clock time so a late frame doesn't shorten the tween.
                var stepStartedAt = TimeSpan.FromMilliseconds(TickToWallMs(actionTick));
                var acceptedDirection = _direction;
                AdvanceOneStep(acceptedDirection, stepStartedAt, startTween: true, tweenFrom: SampleInternal(now), now);
                _nextEligibleTick = actionTick + _stepCooldownTicks;
                if (acceptedCount < acceptedSteps.Length)
                {
                    acceptedSteps[acceptedCount] = acceptedDirection;
                }

                // NET3: report the AUTHORED tick (the gate boundary this step fired on) in lockstep with the
                // direction, so the client can stamp the commit with it and the server replays it at its authored
                // time. Same under-report-never-corrupt rule: a too-small buffer drops the report, never the step.
                if (acceptedCount < acceptedTicks.Length)
                {
                    acceptedTicks[acceptedCount] = actionTick;
                }

                // Count every accepted step even if the buffer was too small to record it (under-report rather
                // than mis-count): a too-small buffer can drop a commit but never desync the predicted tile.
                acceptedCount++;
                changed = true;
            }
        }

        _renderPosition = SampleInternal(now);
        return changed;
    }

    // Applies ONE accepted step in `direction` from the current predicted tile: moves the tile (iff the
    // destination is walkable; a blocked replay step holds in place, mirroring Tick's wall-hold), bumps
    // PredictedStepSeq, and optionally starts the present-time render tween. The SHARED accepted-step body used
    // by both Tick (live stepping) and Reconcile (re-projection of the in-flight steps from a corrected anchor).
    // PredictedStepSeq bumps EXACTLY once per call — the same event the server's StepSequence (S76) bumps on — so
    // the two stay in lockstep for any sequence of accepted steps. startTween distinguishes a LIVE step (true:
    // record its direction for later in-flight re-projection and start the tween) from a RE-PROJECTION replay
    // (false: the directions are being re-read from the buffer, so don't overwrite it, and the render is set
    // once by the caller after the replay). `now` only matters for sampling.
    private void AdvanceOneStep(Direction8 direction, TimeSpan stepStartedAt, bool startTween, RenderPosition tweenFrom, TimeSpan now)
    {
        var delta = direction.Delta();
        var target = _predictedTile.Offset(delta.X, delta.Y);
        if (IsStepWalkable(delta, target))
        {
            _predictedTile = target;
        }

        _facing = direction;
        _predictedStepSeq++;

        if (startTween)
        {
            _inFlightDir[_predictedStepSeq % InFlightDirCapacity] = direction;
            // UO4: remember the avatar's current travel direction (the most recent LIVE accepted step) so SetIntent
            // can detect a ~180° reversal against it. Re-projection replays (startTween == false) must NOT touch it
            // — they replay historical directions, not a new live heading.
            _lastAcceptedDir = direction;
            _hasAcceptedStep = true;
            StartTween(tweenFrom, RenderPosition.FromTile(_predictedTile), stepStartedAt, _cadenceMs);
        }
    }

    // Samples the present-time render position at now and caches it. Cheap to call every frame.
    public RenderPosition Sample(TimeSpan now)
    {
        _renderPosition = SampleInternal(now);
        return _renderPosition;
    }

    // Re-bases the prediction on an authoritative self-snapshot (S83 — authoritative-while-moving reconcile).
    // confirmedTile is the server's truth for the local entity at serverStepSeq (the recipient-scoped
    // RecipientStepSeq off the snapshot header — the server's count of OUR accepted tile moves at that confirm).
    // Returns the reconciliation outcome so the caller can record divergence telemetry.
    //
    // THE MODEL (the standard "re-base on authoritative state + replay un-acked inputs"): on EVERY confirming
    // snapshot — moving or not — we re-anchor _predictedTile / _predictedStepSeq to the server's confirmed tile,
    // then re-project ONLY the genuinely in-flight steps (the ones the client predicted past the server's
    // confirmed point: serverStepSeq+1 .. PredictedStepSeq, count = PredictedStepSeq - serverStepSeq) FORWARD
    // from that anchor in the CURRENTLY HELD direction. The result is the present predicted tile = confirmed +
    // (in-flight count) steps of held intent.
    //
    // WHY THIS FIXES THE ROOT CAUSE (S83). The predictor flips its held direction INSTANTLY on input; the server
    // sees that direction ONE+ TICK LATER (intent crosses the wire, lands in the server's next poll). So at a
    // turn the two sides feed predictor-tick-N and server-tick-N DIFFERENT inputs and accumulate a different
    // count of accepted moves / a different facing phase — the prediction drifts a tile per turn and spam
    // compounds it. The pre-S83 reconcile could not pull a *while-moving* prediction back: the dominant
    // Matched/benign path touched nothing, the mismatch-replay re-ran the SAME diverged predicted history (so it
    // re-landed on the same wrong head), and the only true convergence was gated on !_moving — a one-way ratchet
    // while the key was held. By re-anchoring to the truth on EVERY snapshot and re-projecting only the CAPPED
    // in-flight count (MaxInFlightLead), divergence is capped at the genuine un-acked amount AT ALL TIMES (spam
    // can't ratchet — the excess that the input-skew over-predicts is corrected toward the server, not
    // re-projected), and when input pauses the server catches up (serverStepSeq -> PredictedStepSeq), the
    // in-flight count drains to 0, and the predicted tile converges EXACTLY onto the confirmed tile — moving or
    // stopped.
    //
    // The CAP is the load-bearing change vs a naive re-anchor+replay: without it, re-projecting the full
    // predictedStepSeq - serverStepSeq reproduces the very over-prediction that the skew generated (the predictor
    // out-steps the delayed server, so the gap grows unbounded). The in-flight steps are re-projected along their
    // RECORDED directions (the path the prediction actually took most recently — see _inFlightDir), not the latest
    // held direction, which would transiently mis-place the head right after a turn.
    //
    // RENDER: the visible avatar glides to the RE-PROJECTED PRESENT tile (confirmed + in-flight), never the bare
    // trailing confirmed tile — the re-anchor is internal. We blend from where the avatar is showing NOW to the
    // re-projected present over one cadence (no backward rubberband), or snap if the jump exceeds the threshold
    // (teleport/knockback/big desync). When the re-projected present equals the tile we were already predicting
    // AND the render isn't moved, it is a benign Matched (the common steady-state trailing confirm) — touch the
    // render nothing. We do NOT withhold/hold predicted steps (the reverted S82 failure mode): prediction runs
    // forward in Tick and is corrected after the fact here.
    public ReconcileOutcome Reconcile(TileCoord confirmedTile, uint serverStepSeq, TimeSpan now)
    {
        // STALE / OUT-OF-ORDER GUARD (S77): ignore a confirm whose step-seq is older than one we already
        // reconciled. The client path already guarantees monotonic delivery (HandleSnapshot drops any snapshot
        // <= the last applied SnapshotSequence, and RecipientStepSeq only ever climbs), so this is defence in
        // depth: were a reordered UDP confirm to slip a LOWER serverStepSeq past us, it would otherwise re-anchor
        // onto a stale tile. Touch nothing.
        if (_hasReconciled && serverStepSeq < _highestReconciledStepSeq)
        {
            return ReconcileOutcome.Matched;
        }

        _hasReconciled = true;
        _highestReconciledStepSeq = serverStepSeq;
        // RESYNC1: remember the authoritative confirmed tile/seq so the manual ForceResync primitive has a target
        // to snap onto. Write-only — the normal reconcile below is byte-for-byte unchanged by this line.
        _lastConfirmedTile = confirmedTile;

        // The in-flight steps are the ones we predicted past the server's confirmed point — the ones genuinely
        // sent-but-not-yet-acked (count = predictedStepSeq - serverStepSeq, when positive).
        //
        // The model differs by mode (UO3):
        //
        //  * SERVER-PACED (Predicted / cosmetic, _clientDriven == false): in-flight is honoured ONLY while the
        //    intent is still held. Once the player has STOPPED there is no held intent to re-project — the steps
        //    predicted past the confirm are OVERSHOOT the now-stopped server will never confirm (their intents
        //    arrived after release) — so we converge DOWN to the confirmed tile. The count is CAPPED at
        //    MaxInFlightLead: a larger lead is the input-arrival skew (the predictor's instantaneous-input step
        //    decisions out-ran the server's delayed-input ones — phantom steps the server never took), a
        //    misprediction we converge toward the server rather than re-project.
        //
        //  * CLIENT-DRIVEN (UoClientDriven, _clientDriven == true): every accepted predicted step was emitted as an
        //    explicit StepCommitRequest the server FOLLOWS, so a predicted-but-unconfirmed step is a GENUINE banked
        //    commit the server is still working through — NOT a guess. Therefore on RELEASE (!_moving) the in-flight
        //    commits are STILL in flight (the server keeps finishing them), so we re-project them FORWARD and the
        //    render settles onto the banked destination as the confirms arrive — instead of collapsing onto the
        //    RTT-behind confirmed tile (the backward-snap-on-release bug). And the lead is NOT the input-skew cap:
        //    it is the real RTT-worth of banked commits, so we re-project the full count (bounded only by the
        //    recorded-direction ring so it can't read stale slots). A genuine reject still corrects: a step the
        //    server actually refused leaves serverStepSeq/confirmedTile off the re-projected path, so the recomputed
        //    present tile diverges and we Correct/Snap as usual — only a real reject moves the render.
        int rawInFlight;
        int cap;
        if (serverStepSeq >= _predictedStepSeq)
        {
            rawInFlight = 0;
            cap = 0;
        }
        else if (_clientDriven)
        {
            // Banked commits are in flight whether the key is held or released — the server follows them either way.
            rawInFlight = (int)(_predictedStepSeq - serverStepSeq);
            cap = InFlightDirCapacity;
        }
        else
        {
            // Server-paced: only while moving, and capped to the genuine un-acked window.
            rawInFlight = _moving ? (int)(_predictedStepSeq - serverStepSeq) : 0;
            cap = MaxInFlightLead;
        }

        var inFlight = Math.Min(rawInFlight, cap);

        var oldPredictedTile = _predictedTile;
        var oldRenderSource = SampleInternal(now);

        // Capture the directions of the MOST RECENT `inFlight` predicted steps (the ones closest to the head:
        // predictedStepSeq-inFlight+1 .. predictedStepSeq) BEFORE re-anchoring, from the recorded buffer — so the
        // re-projection follows the path the prediction ACTUALLY took most recently (not the latest held
        // direction, which would transiently mis-place the head whenever a turn just happened). A step older than
        // the buffer window falls back to the current held direction.
        var replay = new Direction8[inFlight];
        for (var i = 0; i < inFlight; i++)
        {
            var seq = _predictedStepSeq - (uint)(inFlight - i) + 1;
            replay[i] = (_predictedStepSeq - seq) < InFlightDirCapacity
                ? _inFlightDir[seq % InFlightDirCapacity]
                : _direction;
        }

        // Re-anchor on the authoritative truth, then re-project the (capped) in-flight steps forward along their
        // recorded directions. PredictedStepSeq becomes serverStepSeq + the re-projected count (so a capped
        // over-prediction also pulls the seq back into the bounded window — it can't run away). The per-step
        // walkability rule still applies (a re-projected step into a wall holds in place, mirroring Tick).
        _predictedTile = confirmedTile;
        _predictedStepSeq = serverStepSeq;

        foreach (var dir in replay)
        {
            // startTween: false — the render is set ONCE below from the recomputed present tile (a per-step tween
            // mid-re-projection would be wrong), and the buffer must not be overwritten during replay.
            AdvanceOneStep(dir, now, startTween: false, tweenFrom: oldRenderSource, now);
        }

        var presentPos = RenderPosition.FromTile(_predictedTile);
        var correction = ChebyshevDistance(oldPredictedTile, _predictedTile);

        // Benign steady-state: the re-projected present tile is exactly the tile we were already predicting, so
        // the prediction was valid and ahead. Leave the render untouched (no rubberband on the common trailing
        // confirm).
        if (_predictedTile == oldPredictedTile)
        {
            _reconcileMatched++;
            return ReconcileOutcome.Matched;
        }

        if (correction > SnapCorrectionThresholdTiles)
        {
            // Large jump (teleport/knockback/big desync): snap the render instantly rather than smear a long
            // slide. Re-arm the schedule so the very next predicted step happens a full cooldown after this snap
            // and we don't immediately re-diverge from the freshly anchored truth.
            var nowTick = EstimateTick(now.TotalMilliseconds);
            _nextEligibleTick = nowTick + _stepCooldownTicks;

            StartTween(presentPos, presentPos, now, _cadenceMs);
            _renderPosition = presentPos;
            _reconcileSnapped++;
            return ReconcileOutcome.Snapped;
        }

        // S85: re-arm the action gate so this correction can't immediately re-step ahead of the server's timing.
        // Reconcile just re-anchored us onto the server's freshly-confirmed tile (the server is itself on cooldown
        // after that step), but the leftover client schedule may already be ELIGIBLE — when a snapshot lands after
        // our last armed step tick (frames run 60–145 Hz, snapshots 20 Hz), so the very next Tick would step
        // forward again and re-open the gap we just closed: a reconcile/predict oscillation at snapshot rate that
        // amplifies the visible spam wobble. We push the gate out by ONE fresh step cooldown (S98: every action is
        // a step now, so the delay is always _stepCooldownTicks — the exact mirror of the server stamping
        // _nextEligibleTick after its accepted step) but ONLY when the schedule is already eligible — a
        // still-future schedule is valid and is left untouched so the normal cadence (and the steady straight-line
        // path, which returns Matched above and never reaches here) keeps its zero-lag timing.
        var correctedNowTick = EstimateTick(now.TotalMilliseconds);
        if (!_nextEligibleTick.HasValue || _nextEligibleTick.Value <= correctedNowTick)
        {
            _nextEligibleTick = correctedNowTick + _stepCooldownTicks;
        }

        // Small disagreement: blend the render from where we're showing NOW to the re-projected present tile over
        // one cadence so the correction settles smoothly instead of popping. We keep the schedule on its tick grid
        // (re-armed above only if it had gone stale-eligible): if still moving we resume stepping at the armed
        // boundary and keep tracking the server cadence, instead of freezing a full cadence.
        StartTween(oldRenderSource, presentPos, now, _cadenceMs);
        _renderPosition = SampleInternal(now);
        _reconcileCorrected++;
        return ReconcileOutcome.Corrected;
    }

    // DIAG1: zeroes the reconcile-outcome tallies (Matched / Corrected / Snapped) so the human can reset them just
    // before a loss burst and read fresh counts. Measurement only — touches no prediction/reconcile state.
    public void ResetReconcileCounters()
    {
        _reconcileMatched = 0;
        _reconcileCorrected = 0;
        _reconcileSnapped = 0;
    }

    // RESYNC1: manual Force Resync — the reusable resync PRIMITIVE (UO5 tier-2 + NET4 tier-3 will call this; do
    // NOT inline its logic in a UI handler). Hard-resets the local prediction onto the last server-confirmed
    // state the predictor reconciled against, and clears any in-flight / banked-but-unconfirmed state so nothing
    // stale replays forward:
    //   * _predictedTile  -> _lastConfirmedTile      (the authoritative position Reconcile last anchored on)
    //   * _predictedStepSeq -> _highestReconciledStepSeq (the server's confirmed step-seq — re-anchor)
    //   * the render is HARD-SNAPPED to the confirmed tile (no blend — this is an explicit resync, unlike
    //     Reconcile's near-miss present-time blend), and the tween is collapsed onto it so the next Sample/Tick
    //     can't drift toward a stale target.
    //   * the action gate is re-armed one fresh cooldown out (mirrors Reconcile's snap branch) so the very next
    //     predicted step happens a full cooldown after the resync instead of immediately re-opening the gap.
    //   * any armed reversal settle is dropped (no in-flight reversal to honour after a hard snap).
    // After this the in-flight count (PredictedStepSeq - serverStepSeq) is 0, so the NEXT Reconcile re-projects
    // nothing and the banked _inFlightDir slots — which are only ever read in the [serverStepSeq+1 .. predictedSeq]
    // window — are unreachable until genuinely overwritten by fresh live steps.
    //
    // USER-TRIGGERED ONLY: nothing calls this in the normal Tick/Reconcile/SetIntent path, so automatic movement
    // is unchanged. IDEMPOTENT / safe in sync: if the prediction is already on the confirmed tile/seq this snaps
    // tile/seq to the same values and the render onto the tile it already shows — a stable no-op. Before the first
    // Reconcile, _lastConfirmedTile is the initial tile and _highestReconciledStepSeq is 0 (the predictor's start
    // state), so an early resync simply returns the avatar to spawn — still a clean, well-defined snap.
    public void ForceResync()
    {
        _predictedTile = _lastConfirmedTile;
        _predictedStepSeq = _highestReconciledStepSeq;
        _settleReversalArmed = false;

        // Hard-snap the render onto the confirmed tile (no present-time blend) and collapse the tween onto it.
        var at = RenderPosition.FromTile(_predictedTile);
        StartTween(at, at, TimeSpan.Zero, _cadenceMs);
        _renderPosition = at;

        // Re-arm the gate one fresh cooldown out so the next step doesn't immediately re-diverge from the truth we
        // just snapped to (mirrors the Reconcile snap branch). Only when a schedule is armed; an idle predictor
        // (no _nextEligibleTick) stays idle.
        if (_nextEligibleTick.HasValue && _hasEstimatedTick)
        {
            _nextEligibleTick = _lastEstimatedTick + _stepCooldownTicks;
        }
    }

    // Updates facing-only from a confirmed snapshot when the position matched (so a server-side turn with no
    // step still rotates the avatar). Cheap and safe — never moves the predicted tile.
    public void ConfirmFacing(Direction8 facing)
    {
        if (!_moving)
        {
            _facing = facing;
        }
    }

    // S75: walkability of a step from the predicted tile, with diagonal corner-cutting rejected — the EXACT
    // mirror of the server's WorldEntity.IsStepWalkable. The destination must be walkable; a DIAGONAL step (both
    // delta axes non-zero) additionally requires both orthogonally-adjacent tiles it cuts between to be walkable
    // (so the local avatar can't predict a slip through a wall corner the server would reject). Cardinal steps
    // (one axis zero) check the destination only. Uses the same _isWalkable oracle (MmoClient.IsWalkableFor
    // Prediction) the server's grid.IsWalkable is fed from, so server and predictor reject identical diagonals.
    private bool IsStepWalkable(TileCoord delta, TileCoord target)
    {
        if (!_isWalkable(target))
        {
            return false;
        }

        if (delta.X != 0 && delta.Y != 0)
        {
            return _isWalkable(_predictedTile.Offset(delta.X, 0)) && _isWalkable(_predictedTile.Offset(0, delta.Y));
        }

        return true;
    }

    // ---- Tick-grid helpers (S81) -----------------------------------------------------------------------

    // Derives the integer step-cooldown tick count from the ms cadence and tickMs. The ms value is already
    // tick-quantised on the wire (MovementCadence), so a round recovers the exact server tick count; clamped
    // >= 1 (a step always costs at least one tick).
    private void RecomputeTickCounts()
    {
        _stepCooldownTicks = (uint)Math.Max(1, (long)Math.Round(_cadenceMs / _tickMs, MidpointRounding.AwayFromZero));
    }

    // The integer server tick for a wall-clock ms value, BEFORE monotonic smoothing.
    private long EstimateRawTick(double nowMs)
    {
        return _serverTickRef + (long)Math.Floor((nowMs - _wallRefMs) / _tickMs);
    }

    // The integer server tick for a wall-clock ms value, clamped monotonic non-decreasing so snapshot-arrival
    // jitter can never rewind the gate (a rewind would re-process a boundary and double-step).
    private long EstimateTick(double nowMs)
    {
        var raw = EstimateRawTick(nowMs);
        if (_hasEstimatedTick && raw < _lastEstimatedTick)
        {
            raw = _lastEstimatedTick;
        }

        _lastEstimatedTick = raw;
        _hasEstimatedTick = true;
        return raw;
    }

    // The first tick boundary at or after a press at nowMs: serverTickRef + ceil((nowMs - wallRef)/tickMs).
    // `ceil` (not floor+1) is exact when the press lands ON a boundary (S80 proved floor+1 mismatches there).
    private long CeilTick(double nowMs)
    {
        return _serverTickRef + (long)Math.Ceiling((nowMs - _wallRefMs) / _tickMs);
    }

    // The wall-clock ms of a tick boundary (the inverse of EstimateRawTick), for anchoring a step's tween start.
    private double TickToWallMs(long tick)
    {
        return _wallRefMs + (tick - _serverTickRef) * _tickMs;
    }

    private void StartTween(RenderPosition from, RenderPosition to, TimeSpan startedAt, double durationMs)
    {
        _renderFrom = from;
        _renderTo = to;
        _tweenStartedAt = startedAt;
        _tweenDurationMs = Math.Max(1, durationMs);
    }

    private RenderPosition SampleInternal(TimeSpan now)
    {
        var elapsedMs = (now - _tweenStartedAt).TotalMilliseconds;
        if (elapsedMs <= 0)
        {
            return _renderFrom;
        }

        if (elapsedMs >= _tweenDurationMs)
        {
            return _renderTo;
        }

        return RenderPosition.Lerp(_renderFrom, _renderTo, elapsedMs / _tweenDurationMs);
    }

    private static int ChebyshevDistance(TileCoord a, TileCoord b)
    {
        return Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
    }

    public enum ReconcileOutcome : byte
    {
        // The re-projected present tile equalled the tile we were already predicting — the prediction was valid
        // and ahead, no render correction.
        Matched = 0,
        // The re-projected present tile differed: blended toward it at present time over one cadence.
        Corrected = 1,
        // The re-projected present tile differed by more than the snap threshold: snapped the render to it.
        Snapped = 2,
    }
}
