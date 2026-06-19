using Mmo.Shared.Domain;

namespace Mmo.Client.Core;

// Client-side movement prediction for the LOCAL player only (S53 redo, UO/ClassicUO-style). Mirrors the
// server's held-intent step loop (Mmo.Server.Runtime.WorldEntity.TryStep + GameServer.StepHeldMovementIntents)
// so the local avatar moves the instant the player inputs, instead of waiting a round-trip for the server to
// confirm each tile. Remote entities are untouched — they stay pure interpolation. The server remains fully
// authoritative; this is a client-side guess that snaps to the truth on the rare divergence.
//
// Why this is a REDO: attempt 1 rendered the predicted local player THROUGH the buffered TileInterpolator,
// whose job is to render confirmed state ~150 ms IN THE PAST for jitter smoothing. Pushing "where I am NOW"
// through a "show the past" buffer cancelled the snappiness, and feeding it backward correction tiles made it
// oscillate — that was the rubber-band. The fix here: the predictor renders the local player AT the predicted
// tile with its OWN present-time step-tween (old tile center -> new tile center over the step duration), with
// NO playout delay. Reconcile is a plain UO resync: on divergence, retarget the tween at the server's truth
// (a tiny present-time blend for a near miss, an instant snap for a large jump); never queue backward tiles.
//
// Faithful mirror of the server rule:
//   * while the held intent is Moving and the per-step cooldown has elapsed, step ONE tile in the held
//     direction iff the destination tile is walkable (same IsWalkable / no-corner-cutting rule the server
//     and TilePathfinder use). A blocked target keeps the intent and the avatar holds at the wall, exactly
//     like the server (the cooldown is consumed only on an accepted step, matching WorldEntity.TryStep
//     which sets _lastStepTick only when it actually moves).
//   * cooldown is timed in wall-clock ms (the client has the effective cadence in ms, not the server tick),
//     advanced by exactly one cadence per accepted step so a long frame can catch up multiple steps
//     deterministically.
//
// This class is pure and deterministic (no clock of its own, no network), so it unit-tests by feeding a held
// intent + a clock + confirmed tiles and asserting the predicted tile sequence + reconcile outcomes.
public sealed class LocalPlayerPredictor
{
    // A correction larger than this (in tiles, Chebyshev) is treated as a teleport/knockback and snaps the
    // render instantly instead of tweening — anything within is a normal start/stop or single-step
    // disagreement and a short present-time blend. One tile covers the worst-case steady-state in-flight
    // divergence; a couple of tiles of slack absorbs a brief multi-step lag spike without snapping. Above
    // that, a visible jump is unavoidable, so snap cleanly rather than smear a long slide.
    private const int SnapCorrectionThresholdTiles = 3;

    private readonly Func<TileCoord, bool> _isWalkable;

    private TileCoord _predictedTile;
    private Direction8 _facing;
    private bool _moving;
    private Direction8 _direction;
    private double _cadenceMs;
    // S63: wall-clock cost of a turn (facing change with no tile move). A turn advances the next-step schedule
    // by THIS, not a full cadence, so whipping the cursor rotates quickly while settling steps at the normal
    // cadence. Sourced from ServerHello (EffectiveTurnDelayMs, tick-quantised) so it matches the server's
    // TurnDelayTicks exactly — a mismatch here reintroduces the S56 rapid-direction-change snap. Always >= 1ms
    // (a turn is never instant).
    private double _turnDelayMs;
    // Wall-clock time at which the next step's cooldown elapses. Null = no step pending (idle). Stepping
    // advances this by exactly one cadence per step.
    private TimeSpan? _nextStepAt;
    // Wall-clock time at which the next action (step OR turn) becomes eligible — the mirror of the server's
    // WorldEntity._nextEligibleTick. Null until the first action. An accepted step advances it by one cadence;
    // a turn advances it by the (smaller) turn delay (S63). SURVIVES a stop so a quick stop->start respects
    // the time already consumed and never double-steps; gates SetIntent's fresh-start scheduling so a fresh
    // press fires only once this time has arrived (immediately when idle long enough, matching the server).
    private TimeSpan? _nextEligibleAt;

    // ---- Present-time render tween (the snappy part; NOT a playout buffer) ------------------------------
    // The local player is rendered by sampling THIS tween at the current wall-clock time — old tile center ->
    // new tile center over the step duration, started the instant the step is accepted (zero delay). On
    // reconcile divergence the tween is retargeted at the server's truth (blend) or hard-set (snap).
    private RenderPosition _renderFrom;
    private RenderPosition _renderTo;
    private TimeSpan _tweenStartedAt;
    private double _tweenDurationMs;
    private RenderPosition _renderPosition;

    // turnDelayMs defaults to 80 (the server's ServerOptions default) for the legacy 4-arg callers/tests; the
    // client passes the ServerHello-advertised, tick-quantised value so prediction stays in lockstep.
    public LocalPlayerPredictor(TileCoord initialTile, Direction8 facing, double cadenceMs, Func<TileCoord, bool> isWalkable, double turnDelayMs = 80d)
    {
        _isWalkable = isWalkable ?? throw new ArgumentNullException(nameof(isWalkable));
        _predictedTile = initialTile;
        _facing = facing;
        _cadenceMs = Math.Max(1, cadenceMs);
        _turnDelayMs = Math.Max(1, turnDelayMs);
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

    public Direction8 Facing => _facing;

    public bool IsMoving => _moving;

    public double CadenceMs => _cadenceMs;

    public double TurnDelayMs => _turnDelayMs;

    // The present-time render position for the local player: where the avatar is shown RIGHT NOW. Advanced by
    // Tick (per accepted step) and Reconcile (retarget on divergence); read via Sample(now).
    public RenderPosition RenderPosition => _renderPosition;

    // Adopts a new step cadence immediately (S51 MovementSpeedChanged / EntitySpawn). The next pending step
    // keeps its already-scheduled time; subsequent steps use the new cadence. Predicting at the current
    // cadence and switching on the wire message mirrors the server applying the new EffectiveStepCooldown.
    public void SetCadence(double cadenceMs)
    {
        _cadenceMs = Math.Max(1, cadenceMs);
    }

    // S63: adopts a new turn delay immediately (ServerHello arrival / live F4 tuning of move.turnDelayMs). The
    // next pending step keeps its already-scheduled time; subsequent turns use the new delay. Kept in lockstep
    // with the server's TurnDelayTicks (the caller passes the tick-quantised EffectiveTurnDelayMs).
    public void SetTurnDelay(double turnDelayMs)
    {
        _turnDelayMs = Math.Max(1, turnDelayMs);
    }

    // Records the held movement intent (the same state the client sends as a MoveIntent). The step schedule
    // mirrors the server's gate EXACTLY (Mmo.Server.Runtime.WorldEntity.TryStep): the next action is due at
    // _nextEligibleAt (one cadence after the last accepted step, or one turn-delay after the last turn), and a
    // direction change updates the held direction but does NOT bring the next step earlier. So:
    //   * A fresh start from idle is naturally prompt: no step is scheduled (_nextStepAt is null) and the
    //     last action was long ago, so we arm the first step at `now` and Tick fires it immediately — matching
    //     the server, whose cooldown elapsed while the entity stood idle.
    //   * A quick stop->start does NOT double-step: keyup leaves _nextEligibleAt intact, so a re-press re-arms
    //     the next step at that already-computed time, respecting the cadence/turn-delay already consumed
    //     (the server's _nextEligibleTick survives the stop and gates the next action the same way).
    //   * Rapid direction flips while moving keep the running schedule untouched, so the prediction produces
    //     the SAME step count/timing as the server instead of out-stepping it and snapping back.
    // On keyup (moving=false) forward projection stops at once and the avatar holds at the predicted tile
    // (the in-flight tween finishes) until the server's confirmed stop lands.
    public void SetIntent(bool moving, Direction8 direction, TimeSpan now)
    {
        if (moving)
        {
            // Arm the next step only on a true idle->moving transition, at the server's next-eligible time. If
            // that time is already in the past — the genuine fresh-start case — it clamps to `now` and the
            // first tile fires immediately; a quick stop->start lands later, exactly when the time consumed by
            // the last action elapses. A direction change while already moving never reschedules.
            if (!_moving)
            {
                var dueAt = _nextEligibleAt ?? now;
                _nextStepAt = dueAt < now ? now : dueAt;
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
            _nextStepAt = null;
        }
    }

    // Advances the prediction to wall-clock time now: steps one tile per elapsed cooldown while the held
    // intent is moving and the destination is walkable, starting a present-time tween for each accepted step.
    // A blocked target stalls at the wall (cooldown not consumed) exactly like the server. Idempotent within a
    // cadence window: calling it every frame only steps when a cooldown has actually elapsed. Returns true if
    // the predicted tile changed this call. Always samples the render tween forward to now so the avatar
    // glides smoothly between steps.
    public bool Tick(TimeSpan now)
    {
        var changed = false;
        if (_moving && _nextStepAt is not null)
        {
            // Cap the catch-up so a huge clock gap (e.g. a long stall / breakpoint) can't spin a pathological
            // loop; the next snapshot re-bases anyway. 8 mirrors the interpolator's per-sample step cap.
            for (var i = 0; i < 8 && now >= _nextStepAt.Value; i++)
            {
                // Turn-then-move (S59) + turn delay (S63): a step in a direction we don't already face just
                // TURNS (no tile move) — mirrors WorldEntity.TryStep. The next step/turn is freed after the
                // TURN DELAY, not a full cadence, so whipping the cursor rotates quickly while settling steps
                // at the normal cadence. _nextEligibleAt advances to the same time (mirrors the server stamping
                // _nextEligibleTick = turnTick + turnDelay) so a stop->start after a turn re-arms from the
                // turn, not an older step.
                if (_direction != _facing)
                {
                    _facing = _direction;
                    _nextStepAt = _nextStepAt.Value + TimeSpan.FromMilliseconds(_turnDelayMs);
                    _nextEligibleAt = _nextStepAt;
                    continue;
                }

                var delta = _direction.Delta();
                var target = _predictedTile.Offset(delta.X, delta.Y);
                if (!_isWalkable(target))
                {
                    // Blocked: hold at the wall. The cooldown is NOT consumed (the server only advances its
                    // step tick on an accepted move), so we keep facing and re-test next tick / on redirect.
                    _facing = _direction;
                    break;
                }

                _predictedTile = target;
                _facing = _direction;
                // Start the present-time tween from where we are showing NOW (carries any in-flight position
                // so back-to-back steps glide continuously) toward the new tile center, over one cadence,
                // beginning at the step's scheduled time so a late frame doesn't shorten the tween.
                StartTween(SampleInternal(now), RenderPosition.FromTile(target), _nextStepAt.Value, _cadenceMs);
                // Advance the schedule by a full cadence and record the next-eligible time (the server stamps
                // _nextEligibleTick = stepTick + cooldown on accept) so a later stop->start re-arms a full
                // cadence after it, never sooner.
                _nextStepAt = _nextStepAt.Value + TimeSpan.FromMilliseconds(_cadenceMs);
                _nextEligibleAt = _nextStepAt;
                changed = true;
            }
        }

        _renderPosition = SampleInternal(now);
        return changed;
    }

    // Samples the present-time render position at now and caches it. Cheap to call every frame.
    public RenderPosition Sample(TimeSpan now)
    {
        _renderPosition = SampleInternal(now);
        return _renderPosition;
    }

    // Re-bases the prediction on an authoritative self-snapshot: confirmedTile is the server's truth for the
    // local entity. Returns the reconciliation outcome so the caller can record divergence telemetry.
    //
    // UO-style resync. Steady state: confirmedTile equals the predicted tile (LAN zero-lag) or trails it by
    // the in-flight steps the server hasn't processed yet — leave the prediction alone (yanking it back to a
    // trailing confirmation is the rubber-band the design forbids). Otherwise the server diverged (blocked a
    // step we took, took a step we didn't, teleported): adopt the truth as the predicted tile and retarget
    // the present-time tween at it — a short blend for a near miss, an instant snap for a large jump. No
    // backward queuing into a buffer.
    public ReconcileOutcome Reconcile(TileCoord confirmedTile, TimeSpan now)
    {
        if (confirmedTile == _predictedTile)
        {
            return ReconcileOutcome.Matched;
        }

        // The server is simply BEHIND on the same line we're predicting (the confirmed tile is one of the
        // in-flight tiles between our last anchor and the prediction): the prediction is still valid and
        // ahead — leave it, the next snapshots catch up.
        if (_moving && IsBehindOnPredictedLine(confirmedTile))
        {
            return ReconcileOutcome.Matched;
        }

        var correction = ChebyshevDistance(_predictedTile, confirmedTile);
        _predictedTile = confirmedTile;
        // Re-arm: the very next predicted step happens a full cadence after the correction so we don't
        // immediately re-diverge from the freshly anchored truth. Anchor _nextEligibleAt at now + cadence too,
        // so a stop->start after a reconcile respects the cadence from here (consistent with Tick).
        _nextEligibleAt = now + TimeSpan.FromMilliseconds(_cadenceMs);
        if (_moving)
        {
            _nextStepAt = _nextEligibleAt;
        }

        var confirmedPos = RenderPosition.FromTile(confirmedTile);
        if (correction > SnapCorrectionThresholdTiles)
        {
            // Large jump (teleport/knockback/desync): snap the render instantly rather than smear a long slide.
            StartTween(confirmedPos, confirmedPos, now, _cadenceMs);
            _renderPosition = confirmedPos;
            return ReconcileOutcome.Snapped;
        }

        // Small disagreement: blend from where we're showing now to the truth over one cadence so a normal
        // start/stop boundary settles smoothly instead of popping.
        StartTween(SampleInternal(now), confirmedPos, now, _cadenceMs);
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

    // True when confirmedTile sits between the predictor's current position and where it started predicting
    // along the held direction — i.e. the server just hasn't processed the in-flight steps yet. We detect
    // this conservatively: the confirmed tile is reachable from itself to the predicted tile by repeatedly
    // applying the held direction's delta (a straight line of predicted steps). Diagonal and orthogonal both
    // collapse to "same signed delta each step", so a simple walk back from the prediction matches.
    private bool IsBehindOnPredictedLine(TileCoord confirmedTile)
    {
        var delta = _direction.Delta();
        var cursor = _predictedTile;
        // Walk backwards from the prediction up to a bounded number of in-flight steps looking for the
        // confirmed tile. The bound matches the snap threshold so we never silently tolerate a big gap.
        for (var i = 0; i < SnapCorrectionThresholdTiles; i++)
        {
            cursor = cursor.Offset(-delta.X, -delta.Y);
            if (cursor == confirmedTile)
            {
                return true;
            }
        }

        return false;
    }

    private static int ChebyshevDistance(TileCoord a, TileCoord b)
    {
        return Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
    }

    public enum ReconcileOutcome : byte
    {
        // The confirmed tile matched the prediction (or trailed it on the predicted line) — no correction.
        Matched = 0,
        // A small disagreement blended toward the confirmed tile at present time.
        Corrected = 1,
        // A large disagreement snapped the render to the confirmed tile.
        Snapped = 2,
    }
}
