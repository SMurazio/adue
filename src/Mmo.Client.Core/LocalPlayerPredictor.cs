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
    // Wall-clock time at which the next step's cooldown elapses. Null = no step pending (idle, or the first
    // step on a fresh keydown fires immediately). Stepping advances this by exactly one cadence per step.
    private TimeSpan? _nextStepAt;

    // ---- Present-time render tween (the snappy part; NOT a playout buffer) ------------------------------
    // The local player is rendered by sampling THIS tween at the current wall-clock time — old tile center ->
    // new tile center over the step duration, started the instant the step is accepted (zero delay). On
    // reconcile divergence the tween is retargeted at the server's truth (blend) or hard-set (snap).
    private RenderPosition _renderFrom;
    private RenderPosition _renderTo;
    private TimeSpan _tweenStartedAt;
    private double _tweenDurationMs;
    private RenderPosition _renderPosition;

    public LocalPlayerPredictor(TileCoord initialTile, Direction8 facing, double cadenceMs, Func<TileCoord, bool> isWalkable)
    {
        _isWalkable = isWalkable ?? throw new ArgumentNullException(nameof(isWalkable));
        _predictedTile = initialTile;
        _facing = facing;
        _cadenceMs = Math.Max(1, cadenceMs);
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

    // Records the held movement intent (the same state the client sends as a MoveIntent). On a fresh keydown
    // (transition to moving, or a direction change) the next step is scheduled to fire IMMEDIATELY so the
    // first tile is predicted with no round-trip wait. On keyup (moving=false) forward projection stops at
    // once and the avatar holds at the predicted tile (the in-flight tween finishes) until the server's
    // confirmed stop lands.
    public void SetIntent(bool moving, Direction8 direction, TimeSpan now)
    {
        if (moving)
        {
            // Fire the first step of a new press / a redirect immediately; while already moving in the same
            // direction keep the running cooldown so we don't double-step.
            if (!_moving || direction != _direction)
            {
                _nextStepAt = now;
            }

            _moving = true;
            _direction = direction;
            _facing = direction;
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
                _nextStepAt = _nextStepAt.Value + TimeSpan.FromMilliseconds(_cadenceMs);
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
        // immediately re-diverge from the freshly anchored truth.
        if (_moving)
        {
            _nextStepAt = now + TimeSpan.FromMilliseconds(_cadenceMs);
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
