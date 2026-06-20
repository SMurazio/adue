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
// S77 — server-reconciliation by step-sequence + replay (kills the rubberband). A bare confirmed tile is
// ambiguous: the old Reconcile could not tell a benign TRAILING in-flight confirm (the server simply hasn't
// processed the steps that carried us forward, or is replaying an OLD-direction confirm from just before a
// turn) from a genuine divergence, so it re-anchored the render BACKWARD onto stale confirms — the rubberband.
// Now every accepted step bumps PredictedStepSeq in lockstep with the server's WorldEntity.StepSequence (S76),
// and the snapshot carries the recipient's authoritative step-sequence (RecipientStepSeq). Reconcile matches a
// confirm to the EXACT predicted step:
//   * MATCH (history tile at serverStepSeq == confirmedTile, or serverStepSeq older than our history) — the
//     server agrees with what we predicted at that step. Touch NOTHING (tile / schedule / render); just prune
//     the now-confirmed history. This is the common case and what removes the rubberband.
//   * MISMATCH (history tile at serverStepSeq != confirmedTile) — a genuine misprediction. Re-anchor the tile +
//     seq to the confirm, REPLAY the recorded directions of the in-flight steps from the corrected anchor to
//     recompute the present tile, then blend (near miss) or snap (large jump) the render. Returns Corrected /
//     Snapped.
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
    // that, a visible jump is unavoidable, so snap cleanly rather than smear a long slide. S77: this is now
    // ONLY the blend-vs-snap render choice after a positively-identified MISMATCH — the seq match decides
    // whether a correction happens at all, this only decides how it is shown.
    private const int SnapCorrectionThresholdTiles = 3;

    // Bounded history of the most-recent in-flight accepted steps: stepSeq -> (tile arrived at, step
    // direction). Enough to cover the worst realistic in-flight lag (the round-trip's worth of unconfirmed
    // steps plus a few pre-turn steps of an old-direction confirm); a confirm older than the oldest retained
    // entry is treated as a benign already-passed match. 32 is comfortably above the Tick catch-up cap (8) and
    // the per-snapshot step count, so a real desync is never silently tolerated past the window.
    private const int HistoryCapacity = 32;

    // Cap the catch-up so a huge clock gap (e.g. a long stall / breakpoint) can't spin a pathological loop; the
    // next snapshot re-bases anyway. 8 mirrors the interpolator's per-sample step cap. Applied to the number of
    // tick boundaries processed per Tick call.
    private const int MaxTicksPerCall = 8;

    // S82 — the maximum number of accepted steps the prediction may run AHEAD of the latest server-confirmed
    // step-seq before it must HOLD and wait for the next confirm. The disease this caps: Tick runs every frame
    // (~16 ms) but Reconcile only lands per snapshot (~50 ms), so during rapid turn-spam the predictor can
    // accept several un-reconciled steps between reconciles and a per-turn misprediction COMPOUNDS — the lead
    // grew to ~6 in the deterministic repro and, because every while-moving confirm benignly MATCHES the step it
    // confirms, the idle/stop clause never fires while the key stays held, so the lead never recovered (the
    // stuck-state latch). Capping the lead at a small N (the in-flight amount at ~0 latency is ~1-2 steps) means
    // a confirm can ALWAYS pull the prediction back: the predictor never advances more than N past the truth, so
    // a single reconcile re-anchors it. At the bound Tick stops accepting steps for that frame WITHOUT consuming
    // the cooldown (it leaves _nextEligibleTick on the un-fired boundary), so the very next confirm that raises
    // the bound lets the held step fire — no double-step, no per-frame rewind. ONLY engaged once the predictor
    // has been reconciled at least once (_hasReconciled): the pure-stepping parity tests never reconcile and must
    // stay unbounded so they remain faithful tick-grid parity proofs; the real client reconciles on the first
    // snapshot, so the bound arms immediately. 2 covers the steady-state in-flight lead with a tile of slack so a
    // normal LAN round-trip never clips, while still capping a spam runaway tightly enough that one confirm heals.
    private const uint MaxPredictedLeadSteps = 2;

    private readonly Func<TileCoord, bool> _isWalkable;

    // Ring of the last HistoryCapacity accepted steps. _history[seq % HistoryCapacity] holds the entry whose
    // StepSeq == seq while seq is within [oldest, PredictedStepSeq]; older slots are overwritten as new steps
    // land. _historyCount tracks how many entries are currently retained (so the oldest retained seq is
    // PredictedStepSeq - _historyCount + 1). See TryGetHistory / RecordHistory.
    private readonly StepRecord[] _history = new StepRecord[HistoryCapacity];
    private int _historyCount;

    private TileCoord _predictedTile;
    // S77: the predictor's count of ACCEPTED tile moves, the exact mirror of the server's
    // WorldEntity.StepSequence (S76) — bumped ONLY on an accepted step (AdvanceOneStep), never on a turn or a
    // blocked/cooldown step. Reconcile uses it to match a snapshot confirm to the predicted step it
    // corresponds to.
    private uint _predictedStepSeq;
    // S77: the highest serverStepSeq ever fed to Reconcile. A defensive monotonic guard: the client already
    // drops out-of-order snapshots (MmoClient.HandleSnapshot rejects any SnapshotSequence <= the last applied,
    // and RecipientStepSeq is the server's monotonically non-decreasing count of our accepted moves, so a
    // reordered older snapshot never reaches Reconcile), but a reordered confirm carrying a LOWER serverStepSeq
    // would otherwise wrongly trip the idle/stop clause (converge DOWN to a stale tile) or the benign-match. We
    // ignore any Reconcile whose seq is older than one we already processed.
    private uint _highestReconciledStepSeq;
    private bool _hasReconciled;
    private Direction8 _facing;
    private bool _moving;
    private Direction8 _direction;

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

                // Turn-then-move (S59) + turn delay (S63): a step in a direction we don't already face just
                // TURNS (no tile move). The next action is freed after turnDelayTicks (mirrors the server
                // stamping _nextEligibleTick = turnTick + turnDelayTicks), so whipping the cursor rotates
                // quickly while settling steps at the normal cadence.
                if (_direction != _facing)
                {
                    _facing = _direction;
                    _nextEligibleTick = actionTick + _turnDelayTicks;
                    continue;
                }

                // S82 LEAD BOUND: once reconciled at least once, never accept a step that would push the
                // prediction more than MaxPredictedLeadSteps past the latest confirmed server step-seq. At the
                // bound HOLD: break out WITHOUT advancing _nextEligibleTick (the cooldown is NOT consumed), so the
                // next confirm that raises _highestReconciledStepSeq lets this same boundary fire — capping the
                // runaway lead so a single reconcile always re-anchors the prediction (the stuck-state latch fix).
                // The check sits BEFORE the turn/walkable branches because a turn does not advance the step-seq
                // (so it can never breach the lead) and we must still process turns even while holding at the
                // bound; only an accepted MOVE is gated.
                if (_hasReconciled
                    && _direction == _facing
                    && _predictedStepSeq >= _highestReconciledStepSeq
                    && _predictedStepSeq - _highestReconciledStepSeq >= MaxPredictedLeadSteps)
                {
                    break;
                }

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

                // Accepted step: advance the tile + step-seq + history, and start the present-time tween from
                // where we are showing NOW (carries any in-flight position so back-to-back steps glide
                // continuously) toward the new tile center, over one cadence, beginning at the boundary's
                // wall-clock time so a late frame doesn't shorten the tween.
                var stepStartedAt = TimeSpan.FromMilliseconds(TickToWallMs(actionTick));
                AdvanceOneStep(_direction, stepStartedAt, startTween: true, tweenFrom: SampleInternal(now), now);
                _nextEligibleTick = actionTick + _stepCooldownTicks;
                changed = true;
            }
        }

        _renderPosition = SampleInternal(now);
        return changed;
    }

    // Applies ONE accepted step in `direction` from the current predicted tile: moves the tile (iff the
    // destination is walkable; a blocked replay step holds in place, mirroring Tick's wall-hold), bumps
    // PredictedStepSeq, records the step in history, and optionally starts the present-time render tween. The
    // SHARED accepted-step body used by both Tick (live stepping) and Reconcile (replay of the in-flight steps
    // from a corrected anchor). PredictedStepSeq bumps EXACTLY once per call — the same event the server's
    // StepSequence (S76) bumps on — so the two stay in lockstep for any sequence of accepted steps. `now` only
    // matters for sampling; when startTween is false the render is left for the caller to set after replay.
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
        RecordHistory(_predictedStepSeq, _predictedTile, direction);

        if (startTween)
        {
            StartTween(tweenFrom, RenderPosition.FromTile(_predictedTile), stepStartedAt, _cadenceMs);
        }
    }

    // Samples the present-time render position at now and caches it. Cheap to call every frame.
    public RenderPosition Sample(TimeSpan now)
    {
        _renderPosition = SampleInternal(now);
        return _renderPosition;
    }

    // Re-bases the prediction on an authoritative self-snapshot, matched by STEP SEQUENCE (S77). confirmedTile
    // is the server's truth for the local entity at serverStepSeq (the recipient-scoped RecipientStepSeq off
    // the snapshot header — the server's count of OUR accepted tile moves at that confirm). Returns the
    // reconciliation outcome so the caller can record divergence telemetry.
    //
    //   * MATCH — serverStepSeq is older than our retained history (a confirm for a step we already moved past),
    //     OR our history's tile at serverStepSeq equals confirmedTile (the server agrees with what we predicted
    //     at that exact step). Either way the prediction is valid and ahead; prune the now-confirmed history
    //     and touch NOTHING (tile / schedule / render). This is the common case — it removes the rubberband,
    //     because a benign trailing/old-direction confirm now MATCHES the step we predicted instead of being
    //     guessed at by distance.
    //   * MISMATCH — our history's tile at serverStepSeq differs from confirmedTile: a genuine misprediction.
    //     Re-anchor _predictedTile + _predictedStepSeq to the confirm, then REPLAY the recorded directions of
    //     the in-flight steps (serverStepSeq+1 .. old PredictedStepSeq) from the corrected anchor to recompute
    //     the present tile (intent/direction is anchor-independent; re-running it with the same walkability
    //     rules yields the corrected present tile). Then retarget the render: blend over one cadence if the
    //     present delta <= SnapCorrectionThresholdTiles, else snap. Returns Corrected / Snapped.
    public ReconcileOutcome Reconcile(TileCoord confirmedTile, uint serverStepSeq, TimeSpan now)
    {
        // STALE / OUT-OF-ORDER GUARD (S77): ignore a confirm whose step-seq is older than one we already
        // reconciled. The client path already guarantees monotonic delivery (HandleSnapshot drops any snapshot
        // <= the last applied SnapshotSequence, and RecipientStepSeq only ever climbs), so this is defence in
        // depth: were a reordered UDP confirm to slip a LOWER serverStepSeq past us, it would otherwise wrongly
        // fire the idle/stop clause (converge DOWN to a stale tile) or a spurious benign match. Touch nothing.
        if (_hasReconciled && serverStepSeq < _highestReconciledStepSeq)
        {
            return ReconcileOutcome.Matched;
        }

        _hasReconciled = true;
        _highestReconciledStepSeq = serverStepSeq;

        // IDLE / STOP-BOUNDARY clause (S77): the player has STOPPED (!_moving) and the server settled BEHIND
        // our prediction (serverStepSeq < _predictedStepSeq). We over-predicted steps the now-stopped server
        // will never take (the trailing step intents arrived after release), so the server will NEVER emit a
        // confirm at our predicted head — leaving us stranded forward forever. Converge DOWN to the truth:
        // re-anchor tile + seq to the confirm, drop the overshoot history, and blend (small) / snap (large) the
        // render onto it. NO replay — we are stopped, there are no in-flight intent steps to re-run. Gated
        // strictly on !_moving so the while-moving benign-match (the rubberband fix) below is untouched.
        if (!_moving && serverStepSeq < _predictedStepSeq)
        {
            var idleOldTile = _predictedTile;
            var idleRenderSource = SampleInternal(now);
            _predictedTile = confirmedTile;
            _predictedStepSeq = serverStepSeq;
            ResetHistory();
            RecordHistory(_predictedStepSeq, _predictedTile, _facing);

            var idleCorrection = ChebyshevDistance(idleOldTile, confirmedTile);
            var idleConfirmedPos = RenderPosition.FromTile(confirmedTile);
            if (idleCorrection > SnapCorrectionThresholdTiles)
            {
                StartTween(idleConfirmedPos, idleConfirmedPos, now, _cadenceMs);
                _renderPosition = idleConfirmedPos;
                return ReconcileOutcome.Snapped;
            }

            StartTween(idleRenderSource, idleConfirmedPos, now, _cadenceMs);
            _renderPosition = SampleInternal(now);
            return ReconcileOutcome.Corrected;
        }

        // MATCH: the confirm is for a step older than anything we still remember (we already moved well past
        // it), or our recorded tile at that exact step agrees with the server. Discard the now-confirmed
        // history and leave tile / schedule / render untouched — re-anchoring backward onto a step the server
        // is only now catching up to is the rubberband.
        if (serverStepSeq < OldestHistorySeq() || MatchesHistory(serverStepSeq, confirmedTile))
        {
            DiscardHistoryThrough(serverStepSeq);
            return ReconcileOutcome.Matched;
        }

        // MISMATCH: a genuine misprediction at serverStepSeq. Capture the in-flight directions we predicted
        // AFTER that step, re-anchor on the truth, and replay them from the corrected tile to recompute the
        // present tile. The replay buffer is taken from the CURRENT history (before we mutate it).
        var replay = CollectReplayDirections(serverStepSeq);

        // Where the prediction stood BEFORE re-anchoring (the present tile we were showing toward) and where we
        // are currently rendering — the first sizes the blend-vs-snap correction, the second is the render's
        // start point for a blend so it glides from the live position rather than popping.
        var oldPredictedTile = _predictedTile;
        var oldRenderSource = SampleInternal(now);
        _predictedTile = confirmedTile;
        _predictedStepSeq = serverStepSeq;
        ResetHistory();
        RecordHistory(_predictedStepSeq, _predictedTile, _facing);

        foreach (var direction in replay)
        {
            // Replay each in-flight step from the corrected anchor. startTween: false — the render is set once,
            // below, from the recomputed present tile (a per-step tween mid-replay would be wrong).
            AdvanceOneStep(direction, now, startTween: false, tweenFrom: oldRenderSource, now);
        }

        var correction = ChebyshevDistance(oldPredictedTile, _predictedTile);
        var confirmedPos = RenderPosition.FromTile(_predictedTile);
        if (correction > SnapCorrectionThresholdTiles)
        {
            // Large jump (teleport/knockback/big desync): snap the render instantly rather than smear a long
            // slide. Re-arm the schedule so the very next predicted step happens a full cooldown after this snap
            // and we don't immediately re-diverge from the freshly anchored truth (mirrors the server stamping
            // _nextEligibleTick = stepTick + cooldown on the move that produced this truth). The re-arm is
            // confined to this snap branch ON PURPOSE (S71): on a small Corrected reconcile while moving we must
            // NOT freeze a full cadence — that freeze stalled prediction for a cadence while the server kept
            // stepping, turning a 1-tile transient into a multi-tile lag-then-jump.
            var nowTick = EstimateTick(now.TotalMilliseconds);
            _nextEligibleTick = nowTick + _stepCooldownTicks;

            StartTween(confirmedPos, confirmedPos, now, _cadenceMs);
            _renderPosition = confirmedPos;
            return ReconcileOutcome.Snapped;
        }

        // Small disagreement: blend the render from where we're showing now to the recomputed present tile over
        // one cadence so a normal start/stop boundary settles smoothly instead of popping. Crucially (S71) we
        // leave the schedule on its EXISTING tick grid: if moving we resume stepping at the already-armed
        // boundary and keep tracking the server cadence, instead of freezing a full cadence.
        StartTween(oldRenderSource, confirmedPos, now, _cadenceMs);
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

    // ---- Step-seq history ring (S77) -------------------------------------------------------------------

    // The oldest stepSeq still retained in history. When history is empty this is PredictedStepSeq + 1 (an
    // empty window above the current seq), so "serverStepSeq < OldestHistorySeq" is true for any seq <=
    // PredictedStepSeq — i.e. with nothing in flight a confirm at-or-before our seq is a benign match.
    private uint OldestHistorySeq()
    {
        return _historyCount == 0
            ? _predictedStepSeq + 1
            : _predictedStepSeq - (uint)(_historyCount - 1);
    }

    // True when history has an entry for serverStepSeq and its recorded tile equals confirmedTile.
    private bool MatchesHistory(uint serverStepSeq, TileCoord confirmedTile)
    {
        return TryGetHistory(serverStepSeq, out var record) && record.Tile == confirmedTile;
    }

    private bool TryGetHistory(uint seq, out StepRecord record)
    {
        if (_historyCount > 0 && seq >= OldestHistorySeq() && seq <= _predictedStepSeq)
        {
            record = _history[seq % HistoryCapacity];
            if (record.StepSeq == seq)
            {
                return true;
            }
        }

        record = default;
        return false;
    }

    // The directions of the in-flight steps AFTER serverStepSeq, in step order, that must be replayed from the
    // corrected anchor on a mismatch. Reads from the current history before it is reset.
    private Direction8[] CollectReplayDirections(uint serverStepSeq)
    {
        var first = serverStepSeq + 1;
        if (_predictedStepSeq < first)
        {
            return [];
        }

        var count = (int)(_predictedStepSeq - serverStepSeq);
        var directions = new Direction8[count];
        for (var i = 0; i < count; i++)
        {
            var seq = first + (uint)i;
            directions[i] = TryGetHistory(seq, out var record) ? record.Direction : _direction;
        }

        return directions;
    }

    // Records an accepted step (or the re-anchor pseudo-entry) at seq into the ring and counts it as retained.
    private void RecordHistory(uint seq, TileCoord tile, Direction8 direction)
    {
        _history[seq % HistoryCapacity] = new StepRecord(seq, tile, direction);
        if (_historyCount < HistoryCapacity)
        {
            _historyCount++;
        }
    }

    // Drops every retained entry at or below serverStepSeq (now confirmed). Leaves the still-in-flight entries.
    private void DiscardHistoryThrough(uint serverStepSeq)
    {
        if (_historyCount == 0)
        {
            return;
        }

        if (serverStepSeq >= _predictedStepSeq)
        {
            _historyCount = 0;
            return;
        }

        var newOldest = serverStepSeq + 1;
        var retained = (int)(_predictedStepSeq - newOldest + 1);
        if (retained < _historyCount)
        {
            _historyCount = retained;
        }
    }

    private void ResetHistory()
    {
        _historyCount = 0;
    }

    private static int ChebyshevDistance(TileCoord a, TileCoord b)
    {
        return Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
    }

    private readonly record struct StepRecord(uint StepSeq, TileCoord Tile, Direction8 Direction);

    public enum ReconcileOutcome : byte
    {
        // The confirmed tile matched the predicted step at that sequence (or is older than our history) — no
        // correction.
        Matched = 0,
        // A genuine misprediction blended toward the recomputed present tile at present time.
        Corrected = 1,
        // A genuine misprediction snapped the render to the recomputed present tile.
        Snapped = 2,
    }
}
