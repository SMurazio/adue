using Mmo.Shared.Domain;

namespace Mmo.Client.Core;

// Client-side movement prediction for the LOCAL player only (S53). Mirrors the server's held-intent step
// loop (Mmo.Server.Runtime.WorldEntity.TryStep + GameServer.StepHeldMovementIntents) so the local avatar
// moves the instant the player inputs, instead of waiting a round-trip for the server to confirm each
// tile. Remote entities are untouched — they stay pure interpolation. The server remains fully
// authoritative; this is a client-side guess that re-bases off every authoritative self-snapshot.
//
// Faithful mirror of the server rule:
//   * while the held intent is Moving and the per-step cooldown has elapsed, step ONE tile in the held
//     direction iff the destination tile is walkable (same IsWalkable / no-corner-cutting rule the server
//     and TilePathfinder use). A blocked target keeps the intent and the avatar holds at the wall, exactly
//     like the server (the cooldown is consumed only on an accepted step, matching WorldEntity.TryStep
//     which sets _lastStepTick only when it actually moves).
//   * cooldown is timed in wall-clock ms (the client has the effective cadence in ms, not the server
//     tick), advanced by exactly one cadence per accepted step so a long frame can catch up multiple steps
//     deterministically.
//
// Re-base: the server confirms the RESULT tile of its stepping. On each authoritative self-snapshot the
// predictor takes that confirmed tile as the anchor of truth. In the steady state the anchor equals (or
// trails by the in-flight steps) the prediction and nothing visibly changes. On divergence (the server
// blocked a step the client predicted walkable, a cadence change, a teleport/knockback) the prediction is
// corrected toward the anchor — a small correction fast-blends through the interpolator, a large one
// snaps. This class is pure and deterministic (no clock of its own, no network) so it unit-tests by
// feeding a held intent + a clock + confirmed tiles and asserting the predicted tile sequence.
public sealed class LocalPlayerPredictor
{
    // A correction larger than this (in tiles, Chebyshev) is treated as a teleport/knockback and snaps the
    // interpolator instead of tweening — anything within is a normal start/stop or single-step
    // disagreement and fast-blends. One tile covers the worst-case steady-state in-flight divergence; a
    // couple of tiles of slack absorbs a brief multi-step lag spike without snapping. Above that, a visible
    // jump is unavoidable, so snap cleanly rather than smear a long slide.
    private const int SnapCorrectionThresholdTiles = 3;

    private readonly Func<TileCoord, bool> _isWalkable;
    private readonly TileInterpolator _interpolator;

    private TileCoord _predictedTile;
    private Direction8 _facing;
    private bool _moving;
    private Direction8 _direction;
    private double _cadenceMs;
    // Wall-clock time at which the next step's cooldown elapses. Null = no step pending (idle, or the first
    // step on a fresh keydown fires immediately). Stepping advances this by exactly one cadence per step.
    private TimeSpan? _nextStepAt;

    public LocalPlayerPredictor(TileCoord initialTile, Direction8 facing, double cadenceMs, Func<TileCoord, bool> isWalkable, TileInterpolator interpolator)
    {
        _isWalkable = isWalkable ?? throw new ArgumentNullException(nameof(isWalkable));
        _interpolator = interpolator ?? throw new ArgumentNullException(nameof(interpolator));
        _predictedTile = initialTile;
        _facing = facing;
        _cadenceMs = Math.Max(1, cadenceMs);
    }

    // The tile the predictor currently believes the local player occupies. This is the predicted (snappy)
    // position, ahead of the server's last confirmation by the in-flight steps. Harvest/interact targeting
    // must NOT use this (see the design note) — it is for movement rendering only.
    public TileCoord PredictedTile => _predictedTile;

    public Direction8 Facing => _facing;

    public bool IsMoving => _moving;

    public double CadenceMs => _cadenceMs;

    // Adopts a new step cadence immediately (S51 MovementSpeedChanged / EntitySpawn). The next pending step
    // keeps its already-scheduled time; subsequent steps use the new cadence. Predicting at the current
    // cadence and switching on the wire message mirrors the server applying the new EffectiveStepCooldown.
    public void SetCadence(double cadenceMs)
    {
        _cadenceMs = Math.Max(1, cadenceMs);
    }

    // Records the held movement intent (the same state the client sends as a MoveIntent). On a fresh
    // keydown (transition to moving, or a direction change) the next step is scheduled to fire IMMEDIATELY
    // so the first tile is predicted with no round-trip wait. On keyup (moving=false) forward projection
    // stops at once and the avatar holds at the predicted tile until the server's confirmed stop lands.
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
    // intent is moving and the destination is walkable, feeding each accepted step into the interpolator so
    // it tweens immediately. A blocked target stalls at the wall (cooldown not consumed) exactly like the
    // server. Idempotent within a cadence window: calling it every frame only steps when a cooldown has
    // actually elapsed. Returns true if the predicted tile changed this call.
    public bool Tick(TimeSpan now)
    {
        if (!_moving || _nextStepAt is null)
        {
            return false;
        }

        var changed = false;
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
            _nextStepAt = _nextStepAt.Value + TimeSpan.FromMilliseconds(_cadenceMs);
            _interpolator.Confirm(_predictedTile, now);
            changed = true;
        }

        return changed;
    }

    // Re-bases the prediction on an authoritative self-snapshot: confirmedTile is the server's truth for
    // the local entity. Returns the reconciliation outcome so the caller can record divergence telemetry.
    //
    // Steady state: confirmedTile equals the predicted tile (or trails it by the in-flight steps the server
    // hasn't processed yet) — we DON'T yank the prediction back to a trailing confirmation (that would be
    // the rubber-band the design forbids); we only correct when the server has diverged AHEAD of, or off,
    // the prediction in a way the prediction can't reach. Concretely: if the confirmed tile is exactly the
    // predicted tile, no-op. Otherwise the server disagreed (blocked a step we took, took a step we didn't,
    // teleported) — snap the predicted position to the anchor and re-arm stepping from there so subsequent
    // held-intent steps continue correctly off the truth.
    public ReconcileOutcome Reconcile(TileCoord confirmedTile, TimeSpan now)
    {
        if (confirmedTile == _predictedTile)
        {
            return ReconcileOutcome.Matched;
        }

        // If the server is simply BEHIND on the same straight line we're predicting (the confirmed tile is
        // one of the tiles between our last anchor and the prediction, reachable by continuing the held
        // direction), the prediction is still valid and ahead — leave it, the next snapshots will catch up.
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

        if (correction > SnapCorrectionThresholdTiles)
        {
            // Large jump: snap the interpolator to the truth (teleport/knockback/desync) rather than smear.
            _interpolator.Reset(confirmedTile);
            return ReconcileOutcome.Snapped;
        }

        // Small disagreement: feed the truth as the next interpolation target so it fast-blends instead of
        // popping — the interpolator already tweens toward queued tiles at cadence.
        _interpolator.Confirm(confirmedTile, now);
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

    // True when confirmedTile sits between the predictor's current position and where it started predicting
    // along the held direction — i.e. the server just hasn't processed the in-flight steps yet. We detect
    // this conservatively: the confirmed tile is reachable from itself to the predicted tile by repeatedly
    // applying the held direction's delta (a straight line of predicted steps). Diagonal and orthogonal
    // both collapse to "same signed delta each step", so a simple walk back from the prediction matches.
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
        // A small disagreement fast-blended toward the confirmed tile.
        Corrected = 1,
        // A large disagreement snapped the interpolator to the confirmed tile.
        Snapped = 2,
    }
}
