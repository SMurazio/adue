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
//     _nextEligibleTick), it resolves ONE action exactly like WorldEntity.TryStep: a step in a direction we
//     don't already face TURNS (no tile move, +turnDelayTicks); a step in the faced direction MOVES one tile
//     iff walkable (same IsWalkable / no-corner-cutting rule, +stepCooldownTicks and bump PredictedStepSeq); a
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
    private Direction8 _facing;
    private bool _moving;
    private Direction8 _direction;

    // S87 — OPTIONAL input-lag matching (default 0 = instant, byte-identical to the pre-S87 behaviour). The
    // predictor flips its held direction the instant the player inputs, but the server only sees that change
    // ~one arrival-lag later (the MoveIntent crosses the wire + lands in its next tick poll), so during direction
    // spam the two make different turn-vs-step decisions for the SAME tick and the prediction diverges a tile
    // (S83 bounds + corrects it — the visible spam wobble). When _inputLagTicks > 0 we DELAY the effect of a
    // MID-MOVE direction change by that many ticks so the predictor samples the held direction at the SAME tick
    // the server does, cancelling the skew at the source. The idle->move START is recorded backdated by the lag
    // (so a fresh press stays crisp — only changes while already moving pay it). The effective direction at each
    // action boundary is sampled from a small input history keyed by tick. Live-tunable from the client so the
    // feel can be A/B'd without a restart.
    private const int InputHistoryCapacity = 32;
    private readonly (long tick, Direction8 dir)[] _inputHistory = new (long, Direction8)[InputHistoryCapacity];
    private int _inputHistoryHead;   // next write slot (ring)
    private int _inputHistoryCount;
    private uint _inputLagTicks;

    // ---- Tick grid (S81) -------------------------------------------------------------------------------
    // The server's tick interval in ms; the unit of the whole gate. cadence/turn-delay are expressed as an
    // INTEGER number of these ticks so the predictor steps exactly where WorldEntity.TryStep does.
    private double _tickMs;
    // Effective per-step cooldown and per-turn delay in WHOLE ticks, the exact mirror of the server's
    // stepCooldownTicks / turnDelayTicks. Derived from the ms cadence/turn-delay (already tick-quantised on the
    // wire) by rounding to the nearest tick; always >= 1 (a step/turn always costs at least a beat).
    private uint _stepCooldownTicks;
    private uint _turnDelayTicks;
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
    // The earliest server tick at which the next action (step OR turn) may fire — the exact mirror of
    // WorldEntity._nextEligibleTick. Null until the first action is armed (SetIntent on idle->move quantises it
    // to the next tick with ceil). An accepted step sets it to actionTick + stepCooldownTicks; a turn sets it to
    // actionTick + turnDelayTicks. SURVIVES a stop so a quick stop->start respects the cooldown/turn-delay
    // already consumed and never double-steps.
    private long? _nextEligibleTick;

    private double _cadenceMs;
    private double _turnDelayMs;

    // ---- Present-time render tween (the snappy part; NOT a playout buffer) ------------------------------
    // The local player is rendered by sampling THIS tween at the current wall-clock time — old tile center ->
    // new tile center over the step duration, started the instant the step is accepted (zero delay). On
    // reconcile divergence the tween is retargeted at the server's truth (blend) or hard-set (snap).
    private RenderPosition _renderFrom;
    private RenderPosition _renderTo;
    private TimeSpan _tweenStartedAt;
    private double _tweenDurationMs;
    private RenderPosition _renderPosition;

    // turnDelayMs defaults to 80 (the server's ServerOptions default) for the legacy 4-arg callers/tests;
    // tickMs defaults to 50 (the server's 20 Hz tick). The client passes the ServerHello-advertised,
    // tick-quantised cadence/turn-delay and the real tick interval so prediction stays in lockstep.
    public LocalPlayerPredictor(
        TileCoord initialTile,
        Direction8 facing,
        double cadenceMs,
        Func<TileCoord, bool> isWalkable,
        double turnDelayMs = 80d,
        double tickMs = 50d)
    {
        _isWalkable = isWalkable ?? throw new ArgumentNullException(nameof(isWalkable));
        _predictedTile = initialTile;
        _facing = facing;
        _tickMs = Math.Max(1, tickMs);
        _cadenceMs = Math.Max(1, cadenceMs);
        _turnDelayMs = Math.Max(1, turnDelayMs);
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

    public Direction8 Facing => _facing;

    public bool IsMoving => _moving;

    public double CadenceMs => _cadenceMs;

    public double TurnDelayMs => _turnDelayMs;

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

    // S63: adopts a new turn delay immediately (ServerHello arrival / live F4 tuning of move.turnDelayMs).
    // Re-derives the tick-count turn delay; subsequent turns use it. Kept in lockstep with the server's
    // TurnDelayTicks (the caller passes the tick-quantised EffectiveTurnDelayMs).
    public void SetTurnDelay(double turnDelayMs)
    {
        _turnDelayMs = Math.Max(1, turnDelayMs);
        RecomputeTickCounts();
    }

    // S81: adopts the server's tick interval (1000 / TickRate). Re-derives the integer tick counts so the gate
    // mirrors the server's stepCooldownTicks / turnDelayTicks exactly. The client sets this from ServerHello.
    public void SetTickMs(double tickMs)
    {
        _tickMs = Math.Max(1, tickMs);
        RecomputeTickCounts();
    }

    // S87: the input-lag (in whole server ticks) applied to MID-MOVE direction changes so the predictor mirrors
    // the server's one-arrival-lag-delayed view of the held intent — kills the direction-spam wobble at the
    // source, at the cost of a slightly softer turn. 0 = instant (default, unchanged). Live-tunable from the
    // client so the feel can be A/B'd without a restart; takes effect on the next direction change (a still-empty
    // history falls back to the current held direction, so enabling mid-move is a benign no-op until the next
    // flip).
    public uint InputLagTicks => _inputLagTicks;

    public void SetInputLagTicks(uint ticks)
    {
        _inputLagTicks = ticks;
    }

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
    // mirrors the server's gate EXACTLY (Mmo.Server.Runtime.WorldEntity.TryStep): the next action is due at the
    // integer _nextEligibleTick (stepCooldownTicks after the last accepted step, or turnDelayTicks after the
    // last turn), and a direction change updates the held direction but does NOT bring the next action earlier.
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
            // Arm the first action only on a true idle->moving transition. Quantise the press to the next tick
            // boundary with ceil; if a surviving _nextEligibleTick from a prior action is LATER (a quick
            // stop->start), keep it so the cooldown/turn-delay already consumed is respected.
            if (!_moving)
            {
                var pressTick = CeilTick(now.TotalMilliseconds);
                _nextEligibleTick = _nextEligibleTick.HasValue
                    ? Math.Max(_nextEligibleTick.Value, pressTick)
                    : pressTick;
                // S87: a fresh idle->move records the start direction BACKDATED by the lag so the first step is
                // immediately effective (no lag on a fresh press — only mid-move changes pay it). No-op at lag 0.
                if (_inputLagTicks > 0)
                {
                    _inputHistoryHead = 0;
                    _inputHistoryCount = 0;
                    RecordInput(EstimateTick(now.TotalMilliseconds) - _inputLagTicks, direction);
                }
            }
            else if (_inputLagTicks > 0 && direction != _direction)
            {
                // S87: a MID-MOVE direction change is recorded at the current tick so EffectiveDirectionAt samples
                // it _inputLagTicks later, mirroring the server's delayed view of the new intent.
                RecordInput(EstimateTick(now.TotalMilliseconds), direction);
            }

            _moving = true;
            _direction = direction;
            // S59 turn-then-move: do NOT set _facing here. The server changes Facing only inside TryStep at a
            // step boundary (a turn), never at intent-receive; Tick mirrors that. Setting facing here would
            // make Tick see _direction == _facing and skip the turn, desyncing from the server.
        }
        else
        {
            _moving = false;
        }
    }

    // Advances the prediction to wall-clock time now: processes each elapsed tick boundary once, in order,
    // resolving one action per boundary (turn / step / blocked-hold) exactly like the server, while the held
    // intent is moving. Idempotent within a tick window: calling it every frame only acts when a new tick
    // boundary has elapsed. Returns true if the predicted tile changed this call. Always samples the render
    // tween forward to now so the avatar glides smoothly between steps.
    public bool Tick(TimeSpan now)
    {
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

                // S87: the held direction IN EFFECT at this boundary — with input-lag, the input the player held
                // _inputLagTicks ago (so we decide turn-vs-step on the same direction the server does); with lag 0
                // it is just the current held direction (today's behaviour, zero overhead).
                var dir = EffectiveDirectionAt(actionTick);

                // Turn-then-move (S59) + turn delay (S63): a step in a direction we don't already face just
                // TURNS (no tile move). The next action is freed after turnDelayTicks (mirrors the server
                // stamping _nextEligibleTick = turnTick + turnDelayTicks), so whipping the cursor rotates
                // quickly while settling steps at the normal cadence.
                if (dir != _facing)
                {
                    _facing = dir;
                    _nextEligibleTick = actionTick + _turnDelayTicks;
                    continue;
                }

                var delta = dir.Delta();
                var target = _predictedTile.Offset(delta.X, delta.Y);
                if (!IsStepWalkable(delta, target))
                {
                    // Blocked: hold at the wall. The cooldown is NOT consumed (the server only advances its
                    // step tick on an accepted move), so we keep facing and re-test the NEXT tick — advance
                    // _nextEligibleTick by one tick so the loop makes progress without consuming the cooldown.
                    _facing = dir;
                    _nextEligibleTick = actionTick + 1;
                    continue;
                }

                // Accepted step: advance the tile + step-seq (recording its direction for in-flight re-projection),
                // and start the present-time tween from
                // where we are showing NOW (carries any in-flight position so back-to-back steps glide
                // continuously) toward the new tile center, over one cadence, beginning at the boundary's
                // wall-clock time so a late frame doesn't shorten the tween.
                var stepStartedAt = TimeSpan.FromMilliseconds(TickToWallMs(actionTick));
                AdvanceOneStep(dir, stepStartedAt, startTween: true, tweenFrom: SampleInternal(now), now);
                _nextEligibleTick = actionTick + _stepCooldownTicks;
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

        // The in-flight steps are the ones we predicted past the server's confirmed point — the ones genuinely
        // sent-but-not-yet-acked. ONLY while the intent is still held: if the server has confirmed at/beyond our
        // head (serverStepSeq >= predictedStepSeq) nothing is in flight, and once the player has STOPPED there is
        // no held intent to re-project (the steps we predicted past the confirm are OVERSHOOT the now-stopped
        // server will never confirm — their intents arrived after release), so we converge DOWN to the confirmed
        // tile rather than re-project them forward (which would strand the avatar ahead forever).
        //
        // CAP the re-projected count at MaxInFlightLead. The genuine un-acked lead on a sane uplink is ~1 step;
        // a larger predictedStepSeq - serverStepSeq means the predictor's instantaneous-input step decisions
        // out-ran the server's delayed-input ones (the input-arrival skew predicting phantom steps the server
        // never took). Those excess steps are a misprediction, not real in-flight, so we DON'T re-project them —
        // we converge their excess toward the server. This bounds divergence DURING movement (the head can lead
        // by at most the cap) and still converges exactly at rest. It is a reconcile-layer correction AFTER the
        // fact (prediction still runs fully forward in Tick — snappy on input), NOT the reverted S82 forward
        // step-withholding (which added input lag by holding requested steps back).
        var rawInFlight = (!_moving || serverStepSeq >= _predictedStepSeq)
            ? 0
            : (int)(_predictedStepSeq - serverStepSeq);
        var inFlight = Math.Min(rawInFlight, MaxInFlightLead);

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
            return ReconcileOutcome.Snapped;
        }

        // S85: re-arm the action gate so this correction can't immediately re-step ahead of the server's timing.
        // Reconcile just re-anchored us onto the server's freshly-confirmed tile (the server is itself on cooldown
        // after that step), but the leftover client schedule may already be ELIGIBLE — when a snapshot lands after
        // our last armed step tick (frames run 60–145 Hz, snapshots 20 Hz), so the very next Tick would step
        // forward again and re-open the gap we just closed: a reconcile/predict oscillation at snapshot rate that
        // amplifies the visible spam wobble. We push the gate out by ONE fresh action delay (turn-vs-step, the
        // exact mirror of the server stamping _nextEligibleTick after its action) but ONLY when the schedule is
        // already eligible — a still-future schedule is valid and is left untouched so the normal cadence (and the
        // steady straight-line path, which returns Matched above and never reaches here) keeps its zero-lag timing.
        var correctedNowTick = EstimateTick(now.TotalMilliseconds);
        if (!_nextEligibleTick.HasValue || _nextEligibleTick.Value <= correctedNowTick)
        {
            var actionDelay = EffectiveDirectionAt(correctedNowTick) != _facing ? _turnDelayTicks : _stepCooldownTicks;
            _nextEligibleTick = correctedNowTick + actionDelay;
        }

        // Small disagreement: blend the render from where we're showing NOW to the re-projected present tile over
        // one cadence so the correction settles smoothly instead of popping. We keep the schedule on its tick grid
        // (re-armed above only if it had gone stale-eligible): if still moving we resume stepping at the armed
        // boundary and keep tracking the server cadence, instead of freezing a full cadence.
        StartTween(oldRenderSource, presentPos, now, _cadenceMs);
        _renderPosition = SampleInternal(now);
        return ReconcileOutcome.Corrected;
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

    // S87: append an input (the held direction at a server tick) to the bounded history ring.
    private void RecordInput(long tick, Direction8 dir)
    {
        _inputHistory[_inputHistoryHead] = (tick, dir);
        _inputHistoryHead = (_inputHistoryHead + 1) % InputHistoryCapacity;
        if (_inputHistoryCount < InputHistoryCapacity)
        {
            _inputHistoryCount++;
        }
    }

    // S87: the held direction IN EFFECT at an action boundary. With input-lag this is the input the player held
    // (_inputLagTicks) ago — the latest recorded input at or before actionTick - lag — mirroring the server acting
    // on the intent it received one arrival-lag earlier; an input older than the ring window or an empty ring
    // falls back to the current held direction. With lag 0 it is simply the current held direction (the pre-S87
    // path, zero overhead and zero history writes).
    private Direction8 EffectiveDirectionAt(long actionTick)
    {
        if (_inputLagTicks == 0)
        {
            return _direction;
        }

        var sampleTick = actionTick - _inputLagTicks;
        for (var i = 1; i <= _inputHistoryCount; i++)
        {
            var idx = (_inputHistoryHead - i + InputHistoryCapacity) % InputHistoryCapacity;
            if (_inputHistory[idx].tick <= sampleTick)
            {
                return _inputHistory[idx].dir;
            }
        }

        return _direction;
    }

    // ---- Tick-grid helpers (S81) -----------------------------------------------------------------------

    // Derives the integer step-cooldown / turn-delay tick counts from the ms cadence/turn-delay and tickMs.
    // The ms values are already tick-quantised on the wire (MovementCadence), so a round recovers the exact
    // server tick counts; clamped >= 1 (a step/turn always costs at least one tick).
    private void RecomputeTickCounts()
    {
        _stepCooldownTicks = (uint)Math.Max(1, (long)Math.Round(_cadenceMs / _tickMs, MidpointRounding.AwayFromZero));
        _turnDelayTicks = (uint)Math.Max(1, (long)Math.Round(_turnDelayMs / _tickMs, MidpointRounding.AwayFromZero));
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
