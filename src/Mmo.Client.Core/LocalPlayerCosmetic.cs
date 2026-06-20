using Mmo.Shared.Domain;

namespace Mmo.Client.Core;

// S89 — MODEL B "cosmetic lead": a second local-player render driver that runs PARALLEL to LocalPlayerPredictor
// (model A) and is selected by MmoClient.RenderMode at runtime (F5 toggle). A stays the shipped default and is
// behaviorally untouched; B is opt-in.
//
// The three movement models, for shared vocabulary (see docs/movement-input-model.md):
//   * A — full tile prediction (LocalPlayerPredictor). The client owns a PredictedTile AHEAD of the server's
//     confirm and reconciles/re-projects it back. Logic (harvest/targeting) reads the confirmed LocalTile, but a
//     predicted tile exists and the F5 green marker can diverge from magenta. NOT cosmetic.
//   * B — cosmetic lead (THIS class). The ONLY state is the confirmed tile, advanced ONLY on a server ack
//     (Confirm, called from EntityState.ApplySnapshot). The avatar's PIXELS may glide toward the held-input
//     direction early (the snappy part), but NO tile is ever banked ahead for logic — there is no PredictedTile,
//     no step-seq, no Reconcile/replay. A disagreeing confirm CUTS the render to the confirmed tile (no
//     reproject). "No positional prediction," not "no prediction." UO-per-step-approve in spirit: the server
//     gates each tile; the client animates early.
//   * C — full server follow (rejected, not built). Local player treated like a remote (buffered interpolator,
//     playout delay). B is NOT C: B leads early on input; C lags.
//
// By construction B cannot produce model A's at-rest latch or the spam desync: there is no predicted tile, so the
// F5 green (predicted) marker has nothing to diverge from — there is no green tile in B at all.
//
// This class is pure and deterministic (no clock of its own, no network): it unit-tests by feeding SetIntent +
// Tick (wall clock) + Confirm and asserting the render position glides early, never banks a tile, and cuts to a
// disagreeing confirm. It reuses LocalPlayerPredictor's RenderPosition tween idiom (FromTile / Lerp /
// StartTween + SampleInternal) verbatim so a single server step looks identical between A and B.
public sealed class LocalPlayerCosmetic
{
    // The bounded cosmetic lead, in tiles: the render may glide at most this far ahead of the confirmed tile
    // toward the held-input direction before a confirm advances the confirmed tile. 1.0 = exactly one tile (the
    // adjacent tile center). On LAN confirms arrive ~every tick so the cap is rarely reached; at high latency the
    // glide HOLDS at the cap (paced by the confirm rate) until the next confirm.
    private const double CosmeticLeadTiles = 1.0d;

    // The walkability oracle (MmoClient.IsWalkableForPrediction): the SAME one model A's predictor uses, with the
    // S75 diagonal corner-cut rule. Here it gates only the glide DIRECTION (no tile is banked) — a cosmetic gate
    // that keeps B pure while avoiding an ugly glide-into-wall-then-snap on every wall press.
    private readonly Func<TileCoord, bool> _isWalkable;

    // The ONLY authoritative state: the server-confirmed tile. Advanced ONLY in Confirm. Logic never reads
    // anything but this (and it is exactly EntityState.Tile, kept confirmed in both modes).
    private TileCoord _confirmedTile;

    // Cosmetic facing: the held direction while moving (rotate immediately on input), else the confirmed facing.
    private Direction8 _facing;
    private bool _moving;
    private Direction8 _direction;

    // The tile the cosmetic lead is currently gliding TOWARD (the adjacent tile in the held direction), or null
    // when settled on the confirmed tile / not leading. Render-only — never read by logic. Used by Confirm to
    // decide "server agreed with the lead" (seamless) vs "server disagreed" (cut).
    private TileCoord? _leadTarget;

    private double _cadenceMs;

    // ---- Present-time render tween (reused from LocalPlayerPredictor; NOT a playout buffer) -----------------
    private RenderPosition _renderFrom;
    private RenderPosition _renderTo;
    private TimeSpan _tweenStartedAt;
    private double _tweenDurationMs;
    private RenderPosition _renderPosition;

    public LocalPlayerCosmetic(
        TileCoord initialTile,
        Direction8 facing,
        double cadenceMs,
        Func<TileCoord, bool> isWalkable)
    {
        _isWalkable = isWalkable ?? throw new ArgumentNullException(nameof(isWalkable));
        _confirmedTile = initialTile;
        _facing = facing;
        _cadenceMs = Math.Max(1, cadenceMs);
        var at = RenderPosition.FromTile(initialTile);
        _renderFrom = at;
        _renderTo = at;
        _renderPosition = at;
        _tweenDurationMs = _cadenceMs;
    }

    // The server-confirmed tile — the ONLY state B owns. Exposed read-only so a test can assert B banks nothing
    // (it changes only on Confirm). This is NOT a predicted tile; logic reads EntityState.Tile (the same value).
    public TileCoord ConfirmedTile => _confirmedTile;

    // The cosmetic facing: held direction while moving, else the confirmed facing.
    public Direction8 Facing => _facing;

    public bool IsMoving => _moving;

    public double CadenceMs => _cadenceMs;

    // The present-time render position for the local player: where the avatar is shown RIGHT NOW. Advanced by
    // Tick (the cosmetic lead glide) and Confirm (retarget on ack); read via Sample(now).
    public RenderPosition RenderPosition => _renderPosition;

    // Adopts a new step cadence immediately (MovementSpeedChanged / EntitySpawn). The next glide/confirm tween
    // uses it. Mirrors LocalPlayerPredictor.SetCadence so the call site is uniform.
    public void SetCadence(double cadenceMs)
    {
        _cadenceMs = Math.Max(1, cadenceMs);
    }

    // B does NOT run the server tick gate — it glides on wall-clock cadence and is corrected by confirms — so
    // these are no-ops, provided only so the call sites that drive the predictor's tick grid stay uniform.
    public void CalibrateToServerTick(long serverTick, TimeSpan receivedAt)
    {
    }

    public void SetTurnDelay(double turnDelayMs)
    {
    }

    public void SetTickMs(double tickMs)
    {
    }

    // Records the held movement intent (the same state the client sends as a MoveIntent). Unlike the predictor,
    // this NEVER arms a tile step — it only records the held direction (cosmetic facing rotates immediately) so
    // Tick can extend the cosmetic lead glide. On keyup it stops extending the lead; the glide settles back onto
    // the confirmed tile.
    public void SetIntent(bool moving, Direction8 direction, TimeSpan now)
    {
        if (moving)
        {
            _moving = true;
            _direction = direction;
            _facing = direction; // cosmetic: rotate immediately on input.
        }
        else
        {
            _moving = false;
            // S91: on release, SNAP instantly to the confirmed-tile center instead of tweening back over a
            // cadence (the old ~150ms backward drift felt wrong). The confirmed tile IS truth, so locking the
            // render straight onto it is exact — no latch is possible. A degenerate same-from/to tween makes
            // SampleInternal(now) return the center immediately on any subsequent Sample/Tick.
            _leadTarget = null;
            var center = RenderPosition.FromTile(_confirmedTile);
            StartTween(center, center, now, _cadenceMs);
            _renderPosition = center;
        }
    }

    // Advances the COSMETIC render to wall-clock time now. While moving, once the render has settled on the
    // confirmed tile, begin (or continue) gliding from the confirmed tile toward the ADJACENT tile in the held
    // direction, bounded to CosmeticLeadTiles ahead — walkability-gated on the glide direction. NO tile is ever
    // banked; the confirmed tile is untouched here. Returns true if a new lead glide was started this call.
    // Always samples the tween forward to now so the avatar glides smoothly.
    public bool Tick(TimeSpan now)
    {
        var startedLead = false;
        if (_moving)
        {
            var delta = _direction.Delta();
            var adjacent = _confirmedTile.Offset(delta.X, delta.Y);

            // Cosmetic walkability gate: only lead toward a tile the server's same oracle says is walkable
            // (S75 diagonal corner-cut rule mirrored). A blocked adjacent tile => no early glide (the avatar
            // waits on the confirmed tile instead of gliding into a wall and snapping back).
            if (IsLeadWalkable(delta, adjacent))
            {
                // Arm the lead toward the adjacent tile once we're settled on the confirmed tile (or already
                // leading toward this same tile). If the held direction changed, re-target from where we are NOW
                // toward the new adjacent tile so a turn glides smoothly instead of jumping.
                if (_leadTarget != adjacent)
                {
                    _leadTarget = adjacent;
                    StartTween(SampleInternal(now), RenderPosition.FromTile(adjacent), now, _cadenceMs);
                    startedLead = true;
                }
            }
            else
            {
                // Blocked ahead: do not lead. Settle onto the confirmed tile (no glide-into-wall).
                if (_leadTarget is not null)
                {
                    _leadTarget = null;
                    StartTween(SampleInternal(now), RenderPosition.FromTile(_confirmedTile), now, _cadenceMs);
                }
            }
        }

        _renderPosition = ClampLead(SampleInternal(now));
        return startedLead;
    }

    // Samples the present-time render position at now (clamped to the cosmetic-lead bound) and caches it. Cheap
    // to call every frame.
    public RenderPosition Sample(TimeSpan now)
    {
        _renderPosition = ClampLead(SampleInternal(now));
        return _renderPosition;
    }

    // Applies an authoritative self-snapshot (the server ack) — the ONLY place the confirmed tile advances. This
    // is the cut/snap reconciliation: there is no step-seq, no replay, no re-project.
    //   * If the new confirmed tile is the tile the lead was gliding toward (server agreed): retarget the tween
    //     from the CURRENT render position toward the new confirmed-tile center over one cadence, so consecutive
    //     confirmed steps glide continuously (identical to one server step today). Re-arm the lead so the glide
    //     flows straight on into the next adjacent tile.
    //   * Otherwise (blocked / a different tile than the lead headed for): CUT the render to the confirmed tile —
    //     a short ≤1-cadence blend from where we're showing now, so the correction settles within one cadence
    //     without a step-seq reproject. This is the only correction in B.
    public void Confirm(TileCoord confirmedTile, Direction8 facing, TimeSpan now)
    {
        var agreedWithLead = _leadTarget is { } lead && lead == confirmedTile;
        _confirmedTile = confirmedTile;

        // Cosmetic facing: hold the held direction while moving, else adopt the confirmed facing.
        if (!_moving)
        {
            _facing = facing;
        }

        // Retarget the glide from where we are showing NOW toward the new confirmed-tile center over one cadence.
        // When the server agreed with the lead this flows seamlessly into the confirmed step; when it disagreed
        // this is the cut — a bounded ≤1-cadence blend back to the confirmed tile, never an overshoot that
        // persists. Either way the destination is the confirmed tile (truth), so no reproject and no banked tile.
        StartTween(SampleInternal(now), RenderPosition.FromTile(confirmedTile), now, _cadenceMs);
        // The lead is consumed by this confirm; Tick re-arms it next frame toward the new adjacent tile if still
        // moving (and walkable), so a continuing walk keeps gliding without a stall.
        _ = agreedWithLead;
        _leadTarget = null;
        _renderPosition = SampleInternal(now);
    }

    // Re-seeds the driver from the local entity's current confirmed tile + current render position on a LIVE
    // mode switch (F5), so flipping A<->B mid-session doesn't pop the avatar: the new driver starts exactly where
    // the old one was showing, then glides from there.
    public void ReanchorTo(TileCoord confirmedTile, Direction8 facing, RenderPosition currentRender, TimeSpan now)
    {
        _confirmedTile = confirmedTile;
        _facing = facing;
        _leadTarget = null;
        StartTween(currentRender, currentRender, now, _cadenceMs);
        _renderPosition = currentRender;
    }

    // S75 walkability of the lead step from the confirmed tile, with diagonal corner-cutting rejected — the same
    // rule LocalPlayerPredictor.IsStepWalkable uses. The destination must be walkable; a DIAGONAL lead also
    // requires both orthogonally-adjacent cut tiles to be walkable. Purely gates the cosmetic glide direction —
    // no tile is banked.
    private bool IsLeadWalkable(TileCoord delta, TileCoord target)
    {
        if (!_isWalkable(target))
        {
            return false;
        }

        if (delta.X != 0 && delta.Y != 0)
        {
            return _isWalkable(_confirmedTile.Offset(delta.X, 0)) && _isWalkable(_confirmedTile.Offset(0, delta.Y));
        }

        return true;
    }

    // Clamps a sampled render position so it never glides more than CosmeticLeadTiles ahead of the confirmed
    // tile (the soft "hold at the cap" at high latency). The tween itself targets at most the adjacent tile, so
    // this is a belt-and-braces bound on the per-axis lead distance from the confirmed center.
    private RenderPosition ClampLead(RenderPosition pos)
    {
        var dx = Math.Clamp(pos.X - _confirmedTile.X, -CosmeticLeadTiles, CosmeticLeadTiles);
        var dy = Math.Clamp(pos.Y - _confirmedTile.Y, -CosmeticLeadTiles, CosmeticLeadTiles);
        return new RenderPosition(_confirmedTile.X + dx, _confirmedTile.Y + dy);
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
}
