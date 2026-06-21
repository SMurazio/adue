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
// S103: what a release (SetIntent moving=false) decided. When ShouldCommit is true the caller (MmoClient) must
// send a StepCommitRequest for Direction (the render is already gliding to CommitTarget, no snap). When false the
// release took the normal S102 path (snap or soft-settle) and the caller sends nothing extra.
public readonly record struct CosmeticReleaseDecision(bool ShouldCommit, TileCoord CommitTarget, Direction8 Direction);

public sealed class LocalPlayerCosmetic
{
    // The bounded cosmetic lead, in tiles: the render may glide at most this far ahead of the confirmed tile
    // toward the held-input direction before a confirm advances the confirmed tile. 1.0 = exactly one tile (the
    // adjacent tile center) — the current behaviour and the MAX meaningful lead in the single-tile cosmetic model.
    // On LAN confirms arrive ~every tick so the cap is rarely reached; at high latency the glide HOLDS at the cap
    // (paced by the confirm rate) until the next confirm.
    //
    // S94: live-tunable [0.0, 1.0] (was the const CosmeticLeadTiles = 1.0). The forward glide still targets the
    // adjacent tile center; ClampLead caps the SAMPLED render at MaxLeadTiles from the confirmed tile, so this
    // value controls how far the visible lead REACHES before holding. 0.0 ≈ no visible lead (render stays on the
    // confirmed center, like accept/deny); 1.0 = one full tile (current model B, byte-for-byte). Values > 1 would
    // require multi-tile cosmetic prediction (a banked-ahead tile), which is out of scope here. Default 1.0 keeps
    // model B unchanged. Set via MmoClient.SetCosmeticLeadTiles (F5 "Cosmetic lead (tiles)"); clamped on set.
    public double MaxLeadTiles
    {
        get => _maxLeadTiles;
        set => _maxLeadTiles = Math.Clamp(value, 0.0d, 1.0d);
    }

    private double _maxLeadTiles = 1.0d;

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

    // S92: whether the forward cosmetic lead is enabled. true (default) = model B: Tick arms the early glide and
    // release SNAPS to the confirmed tile (S89/S91), unchanged. false = "accept/deny only": Tick never arms the
    // lead (the render only moves via Confirm — the accepted-step tween), release does NOT snap (there is no lead
    // overshoot to unwind), so the avatar moves ONLY on a confirmed step. Settable so a live F5 switch flips it
    // without re-creating the driver.
    public bool LeadEnabled { get; set; } = true;

    // S102: whether model B's release SNAP-to-confirmed (S91) is performed. true (default) = current behavior: on
    // keyup the render locks instantly onto the confirmed-tile center (no backward drift). false = let the in-flight
    // glide settle on its own — the release tween (already toward a confirmed-or-adjacent tile) finishes instead of
    // a hard snap, for a softer release. Only consulted in the LeadEnabled (model B) release branch; AcceptDeny never
    // snapped anyway. Settable live via MmoClient.SetSnapOnRelease so an F6 toggle flips it without re-creating the
    // driver. Note this only changes the release feel; the forward lead and confirm-cut are untouched.
    public bool SnapOnRelease { get; set; } = true;

    // S103 commit-step on release. When true (default), releasing the key while the cosmetic lead has glided PAST
    // CommitThreshold onto the next (walkable) tile does NOT snap back: the render keeps tweening to that tile at
    // normal cadence (a smooth completion of an already-~70%-done step) and the client sends a server-validated
    // commit-step request. Accept (the confirmed tile reaches that tile) = seamless; reject (the server does not
    // step) = the client snaps back. Below the threshold (or into a wall) the existing S102 release behaviour
    // applies — the commit is an ADDITIONAL release path, not a replacement. Only consulted in the LeadEnabled
    // (model B) release branch. Settable live (F6) without re-creating the driver.
    public bool CommitStepEnabled { get; set; } = true;

    // S103: the lead-progress threshold (0..1) at which a release triggers a commit-step instead of the normal
    // release. Default 0.7 ≈ "almost entirely on the next tile". Clamped [0, 1] on set. The server's
    // CommitAcceptFraction (0.5) sits below this in cooldown-elapsed terms so a genuine release past this
    // threshold is always accepted server-side.
    public double CommitThreshold
    {
        get => _commitThreshold;
        set => _commitThreshold = Math.Clamp(value, 0.0d, 1.0d);
    }

    private double _commitThreshold = 0.55d;

    // S103: a pending commit-step is in flight — release-past-threshold kept the render gliding toward _leadTarget
    // (now _pendingCommitTarget) and the client sent a commit request. While true, Tick must NOT re-arm a fresh
    // lead (the key is up). Cleared by ClearPendingCommit (accept = leave render; reject = the client snaps it).
    public bool HasPendingCommit { get; private set; }
    private TileCoord _pendingCommitTarget;

    // S103: the lead progress (0..1) of the SAMPLED render from the confirmed tile toward _leadTarget, at the LAST
    // sample. 0 = on the confirmed tile, 1 = fully on the lead/adjacent tile. Read by SetIntent (release) to decide
    // whether the lead is far enough onto the next tile to commit instead of snap. 0 when not leading.
    public double LeadProgress { get; private set; }

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

    public void SetTickMs(double tickMs)
    {
    }

    // Records the held movement intent (the same state the client sends as a MoveIntent). Unlike the predictor,
    // this NEVER arms a tile step — it only records the held direction (cosmetic facing rotates immediately) so
    // Tick can extend the cosmetic lead glide. On keyup it stops extending the lead; the glide settles back onto
    // the confirmed tile.
    public CosmeticReleaseDecision SetIntent(bool moving, Direction8 direction, TimeSpan now)
    {
        if (moving)
        {
            _moving = true;
            _direction = direction;
            _facing = direction; // cosmetic: rotate immediately on input.
            // S103: a fresh keydown supersedes any in-flight commit — Tick re-arms a normal lead this frame, so the
            // pending-commit state must not linger (it would skew LeadProgress toward the old committed tile).
            HasPendingCommit = false;
            return default;
        }
        else
        {
            _moving = false;

            // S103 commit-step on release. BEFORE the normal release branches: if the lead has glided past the
            // commit threshold onto the next (walkable) tile, do NOT snap or settle — keep the render tweening to
            // that tile at the normal cadence (a smooth completion of the already-~70%-done step) and tell the
            // caller to send a server-validated commit. Sample the progress at `now` first (so a release between
            // ticks sees the up-to-date lead). Only in the LeadEnabled (model B) path; AcceptDeny has no lead.
            if (LeadEnabled && CommitStepEnabled && _leadTarget is { } leadTile)
            {
                _renderPosition = ClampLead(SampleInternal(now));
                LeadProgress = ComputeLeadProgress(_renderPosition, leadTile);
                // Walkability was already gated when the lead was armed (Tick only arms a walkable lead), so a live
                // _leadTarget is walkable. Commit only when the render is far enough onto the next tile.
                if (LeadProgress >= _commitThreshold && IsLeadStillWalkable(leadTile))
                {
                    // Keep the existing tween running to the lead tile (do NOT re-StartTween — that would reset the
                    // progress); just mark the commit pending so Tick stops re-arming a fresh lead while the key is
                    // up. The render finishes the glide at normal speed; the confirm (accept) or the client's
                    // snap-back (reject) resolves it.
                    HasPendingCommit = true;
                    _pendingCommitTarget = leadTile;
                    var committedDirection = _direction;
                    _leadTarget = leadTile; // unchanged — kept so an agreeing Confirm flows seamlessly.
                    return new CosmeticReleaseDecision(true, leadTile, committedDirection);
                }
            }

            if (LeadEnabled && SnapOnRelease)
            {
                // S91 (model B): on release, SNAP instantly to the confirmed-tile center instead of tweening back
                // over a cadence (the old ~150ms backward drift felt wrong). The confirmed tile IS truth, so
                // locking the render straight onto it is exact — no latch is possible. A degenerate same-from/to
                // tween makes SampleInternal(now) return the center immediately on any subsequent Sample/Tick.
                _leadTarget = null;
                var center = RenderPosition.FromTile(_confirmedTile);
                StartTween(center, center, now, _cadenceMs);
                _renderPosition = center;
            }
            else if (LeadEnabled)
            {
                // S102 (SnapOnRelease == false): no hard snap. Stop extending the lead and let the render GLIDE back
                // onto the confirmed-tile center over one cadence from where it is showing now — a soft settle (the
                // pre-S91 release feel). The destination is still the confirmed tile (truth), so no overshoot
                // persists; it just unwinds smoothly instead of cutting.
                _leadTarget = null;
                var center = RenderPosition.FromTile(_confirmedTile);
                StartTween(SampleInternal(now), center, now, _cadenceMs);
            }
            // S92 accept/deny (LeadEnabled == false): do NOT snap. There is no forward lead to unwind, and any
            // in-progress tween is always toward a confirmed (truth) tile, so letting it finish is correct and
            // avoids a release discontinuity. _leadTarget is already null (Tick never armed it). The render keeps
            // moving only via Confirm.
            return default;
        }
    }

    // Advances the COSMETIC render to wall-clock time now. While moving, once the render has settled on the
    // confirmed tile, begin (or continue) gliding from the confirmed tile toward the ADJACENT tile in the held
    // direction, bounded to MaxLeadTiles ahead — walkability-gated on the glide direction. NO tile is ever
    // banked; the confirmed tile is untouched here. Returns true if a new lead glide was started this call.
    // Always samples the tween forward to now so the avatar glides smoothly.
    public bool Tick(TimeSpan now)
    {
        var startedLead = false;
        // S92 accept/deny (LeadEnabled == false): never arm/extend the forward lead. _leadTarget stays null and
        // the render moves ONLY via Confirm (the accepted-step tween) — we just sample the in-flight tween forward
        // below. Model B (LeadEnabled == true) runs the unchanged S89/S91 glide-arming block.
        if (_moving && LeadEnabled)
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
        UpdateLeadProgress();
        return startedLead;
    }

    // Samples the present-time render position at now (clamped to the cosmetic-lead bound) and caches it. Cheap
    // to call every frame.
    public RenderPosition Sample(TimeSpan now)
    {
        _renderPosition = ClampLead(SampleInternal(now));
        UpdateLeadProgress();
        return _renderPosition;
    }

    // S103: clears a pending commit (used by MmoClient when the commit resolves). On ACCEPT the render is already
    // gliding/settled on the committed tile, so we just drop the pending flag and leave the render. On REJECT the
    // caller snaps back via SnapTo. Either way the lead is consumed (a new lead re-arms next Tick if moving).
    public void ClearPendingCommit()
    {
        HasPendingCommit = false;
        LeadProgress = 0d;
    }

    // S103: the pending commit's target tile (the tile the render is finishing the glide toward). Valid only while
    // HasPendingCommit; used by MmoClient to detect "the confirmed tile reached the commit target" = accepted.
    public TileCoord PendingCommitTarget => _pendingCommitTarget;

    // S103: hard-snaps the render to a tile center immediately (the REJECT path — the server did not honour the
    // commit, so cut back to the confirmed tile exactly like a normal disagreeing release). Mirrors the S91 snap:
    // a degenerate same-from/to tween makes any later Sample(now) return the center. Clears any pending commit and
    // the lead so nothing re-leads while the key is up.
    public void SnapTo(TileCoord tile, TimeSpan now)
    {
        HasPendingCommit = false;
        LeadProgress = 0d;
        _leadTarget = null;
        var center = RenderPosition.FromTile(tile);
        StartTween(center, center, now, _cadenceMs);
        _renderPosition = center;
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
        var previousConfirmedTile = _confirmedTile;
        _confirmedTile = confirmedTile;

        // Cosmetic facing: hold the held direction while moving, else adopt the confirmed facing.
        if (!_moving)
        {
            _facing = facing;
        }

        // S103: while a commit is pending the key is UP and the render is finishing the glide to the committed
        // tile. A confirm that has NOT yet reached the committed tile must NOT cut the render back to the (still
        // old) confirmed tile — that would misread a not-yet-arrived accept as a reject (the exact highest-risk
        // race). So: if the confirm reaches the committed tile, the commit is ACCEPTED — clear pending and flow
        // the glide seamlessly onto it (the normal retarget below). If the confirm is for some OTHER tile, the
        // server moved us somewhere unexpected (e.g. a held intent that slipped through) — treat that as the
        // resolution: retarget to it and clear pending. If the confirm is the UNCHANGED old tile (commit not yet
        // processed), leave the in-flight glide to the committed tile untouched and return — MmoClient's grace
        // (RecipientStepSeq advance) owns the eventual reject snap-back.
        if (HasPendingCommit)
        {
            if (confirmedTile == _pendingCommitTarget)
            {
                HasPendingCommit = false;
                LeadProgress = 0d;
            }
            else if (confirmedTile == previousConfirmedTile)
            {
                // An unchanged confirm at the OLD tile (the commit has not been processed yet): keep finishing the
                // glide to the committed tile, do NOT cut back. MmoClient's grace owns the eventual reject.
                _renderPosition = SampleInternal(now);
                return;
            }
            else
            {
                // The server confirmed a different tile than we committed to: resolve the pending commit here and
                // cut to it below (no separate snap-back needed).
                HasPendingCommit = false;
                LeadProgress = 0d;
            }
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
        HasPendingCommit = false;
        LeadProgress = 0d;
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

    // S103: re-walkability-checks the committed/lead tile at release time (the map can't change, but this keeps the
    // commit gate explicit and symmetric with Tick's arm-time check). Recomputes the step delta from the confirmed
    // tile to the lead tile and applies the S75 corner-cut rule.
    private bool IsLeadStillWalkable(TileCoord leadTile)
    {
        var delta = new TileCoord(leadTile.X - _confirmedTile.X, leadTile.Y - _confirmedTile.Y);
        return IsLeadWalkable(delta, leadTile);
    }

    // S103: lead progress (0..1) of the VISIBLE render from the confirmed tile toward `leadTile`. 1 = fully on the
    // lead tile. Computed along the step axis (the lead is exactly one tile away on each non-zero axis), so a
    // diagonal lead measures progress on whichever axis it travels (both advance together). Clamped [0,1].
    private double ComputeLeadProgress(RenderPosition render, TileCoord leadTile)
    {
        var dx = leadTile.X - _confirmedTile.X;
        var dy = leadTile.Y - _confirmedTile.Y;
        var progress = 0d;
        var measured = false;
        if (dx != 0)
        {
            progress = Math.Max(progress, (render.X - _confirmedTile.X) / dx);
            measured = true;
        }

        if (dy != 0)
        {
            progress = Math.Max(progress, (render.Y - _confirmedTile.Y) / dy);
            measured = true;
        }

        return measured ? Math.Clamp(progress, 0d, 1d) : 0d;
    }

    // S103: refreshes LeadProgress from the cached render position toward the live lead/pending-commit target. Kept
    // current on every Tick/Sample so a release reads an up-to-date value without a re-sample.
    private void UpdateLeadProgress()
    {
        var target = HasPendingCommit ? (TileCoord?)_pendingCommitTarget : _leadTarget;
        LeadProgress = target is { } t ? ComputeLeadProgress(_renderPosition, t) : 0d;
    }

    // Clamps a sampled render position so it never glides more than MaxLeadTiles ahead of the confirmed tile
    // (the soft "hold at the cap" at high latency, and the S94 lever's bound). The tween itself targets at most
    // the adjacent tile, so for MaxLeadTiles < 1 this actively caps the per-axis lead distance from the confirmed
    // center (MaxLeadTiles == 0 holds the render on the confirmed tile while moving — no visible lead).
    private RenderPosition ClampLead(RenderPosition pos)
    {
        var dx = Math.Clamp(pos.X - _confirmedTile.X, -_maxLeadTiles, _maxLeadTiles);
        var dy = Math.Clamp(pos.Y - _confirmedTile.Y, -_maxLeadTiles, _maxLeadTiles);
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
