using Mmo.Shared.Domain;

namespace Mmo.Client.Core.Continuous;

// CONTINUOUS MIGRATION (Phase 5): the continuous render driver for EVERY remote entity (other players AND
// tile-stepped monsters / resources). It is the float-position analog of the retired TileInterpolator: a
// fixed-delay PLAYOUT BUFFER that renders slightly BEHIND the newest received server position so a smooth glide
// absorbs snapshot arrival jitter. One driver smooths both kinds — a continuous remote player glides along its
// real path; a tile-stepped monster (Velocity=0, the server snaps it tile-to-tile) glides BETWEEN the received
// tiles instead of popping (the continuous replacement for the MonsterHopInterpolator's arc).
//
// DECISION (docs/migration/phase-5-plan.md): INTERPOLATION ONLY — no extrapolation. Extrapolation (velocity
// dead-reckoning) does nothing for a Velocity=0 tile-stepped monster (the dominant remote case until Phase 8) and
// needs a per-entity velocity wire-add that Phase 3 deferred. So on STARVATION (no future sample to interpolate
// toward) we HOLD at the newest sample rather than fling forward. The deferred-hybrid extrapolator
// (RemoteContinuousEntity) stays unwired for a later velocity-on-wire phase to flip on per-entity.
//
// Pure and Godot-free so it is unit-testable headless. Sample(now) is pure-functional on the buffer + clock — no
// per-frame Advance call is needed; the Godot loop just calls Sample each frame at the current render position.
//
// PORTED from TileInterpolator: the playout-delay machinery (render at now - InterpolationDelay, lerp the two
// bracketing samples) and the CatchUpQueueCap runaway guard, both lifted from the tile/step domain into the
// continuous time domain.
public sealed class RemotePositionInterpolator
{
    // Catch-up cap (ported from TileInterpolator.CatchUpQueueCap, time-domain): a permanent runaway guard. If the
    // playout buffer backs up so the render would trail the NEWEST received sample by more than CatchUpSampleCap
    // buffered confirms (a cadence mismatch, a GC hitch, a tab-out, a live buffer-knob change), drop the oldest
    // stale confirms and keep only the newest tail, so the render can NEVER trail by more than ~this many confirms'
    // worth of motion. At a ~50ms snapshot interval, 8 confirms is ~400ms of tail — comfortably more than the
    // steady-state depth (~InterpolationDelay/interval, a couple of confirms) so a single late/early arrival never
    // trips it, but any genuine pile-up (a burst after a stall) is collapsed to this bound. The collapse is NOT a
    // hard teleport — it keeps a tail of >= 2 confirms so Sample still has a bracket to lerp across (a final glide),
    // and the time-domain playout (now - delay vs real arrival clocks) resumes from the live render position.
    private const int CatchUpSampleCap = 8;

    // The playout buffer of received confirms, oldest-first: (continuous position, airborne height, arrival clock)
    // triples. Sample(now) renders at now - InterpolationDelay by lerping the two buffered confirms that bracket
    // that playout time — BOTH the horizontal position AND the vertical height, on the same brackets/alpha.
    private readonly List<BufferedPosition> _samples = new();

    // Hard cap on buffered samples so a long stall (server silent, render starved) can't grow the buffer
    // unbounded. Generous — far more than the steady-state depth (~InterpolationDelay / snapshot-interval, a
    // handful) so it never clips a legitimate window; the CatchUpSampleCap collapses genuine backlog first.
    private const int MaxBufferedSamples = 256;

    // REMOTE-WALK Phase 2 (v39 dead-reckoning): the hard cap (ms) on how far the starvation branch extrapolates along
    // a sample's replicated velocity before it HOLDs. Bounds the overshoot when a MOVING entity goes silent (a
    // disconnect mid-stride, or a dropped stop confirm) so it glides at most ~MaxExtrapolationMs × speed (≈ one tile
    // at walk speed) and then parks, instead of flinging away. Comfortably above the steady-state extrapolation a
    // walker actually needs (the starvation tail of each ~sample-interval cycle, < the playout delay) so normal
    // dead-reckoning is never clipped; only a genuine signal loss hits the cap.
    private const double MaxExtrapolationMs = 250d;

    // CORRECTION SMOOTHING (S-remote-render-jitter-200-clients): the half-life (ms) of the decaying render-offset
    // that absorbs each NEW SAMPLE's re-base discontinuity. At the default delay-0 extrapolate-to-now setting the
    // render is `newest.Position + newest.Velocity × elapsed`; when a fresh sample lands, that basis jumps by the
    // sample's quantization error + heading change + arrival-timing error — measured live (200-bot stress) as a
    // per-frame >90° velocity flip on ~26% of frames (11% at 120): a visible crowd shimmer. Instead of absorbing
    // the jump in ONE frame, Sample carries it as an offset that decays exponentially (~40ms half-life ⇒ ~12% left
    // after 120ms). LATENCY-FREE by construction: the BASE always tracks the newest sample extrapolated to now —
    // new information moves the render immediately in the right direction; only the ERROR component is spread.
    // (NOT the render-tween dead end: a tween lags ALL motion; this touches only the discrete re-base error, and
    // the smoothness harness pins render-vs-truth + stop-settle so it can never quietly become a laggy tween.)
    private const double CorrectionHalfLifeMs = 40d;

    // A re-base jump larger than this is a genuine discontinuity (teleport / respawn-scale) — snap instead of
    // smoothing a long visible slide. Normal per-sample errors are ~0.01-0.15u; one unit is far beyond them.
    private const double MaxCorrectionUnits = 1.0d;

    // BOSS-1 (docs/boss-encounter-sunderer-design.md): a confirmed position this far from the previous one is a
    // server-authoritative TELEPORT (the /boss arena jump), not motion — hard-RESET onto it (clear the playout
    // buffer, snap) instead of bracket-lerping a slide across the whole map (or hitting the extrapolation cap and
    // freezing-then-popping). The LOCAL predictor already snaps a teleport (SnapThresholdUnits 4u); a REMOTE entity
    // (a teleported partner seen from another client) had NO such snap — Confirm just appended the far sample. This is
    // that missing snap. Threshold well above any legitimate per-sample delta (a walker at 4u/s over a ~50ms snapshot
    // is ~0.2u; a bounded dash/leap a few u) so it never trips on real movement, but far below a cross-map jump (tens
    // to hundreds of u). Same intent as the local predictor's SnapThresholdUnits, one domain over.
    private const double TeleportSnapUnits = 8.0d;

    // Correction offset still being blended out (render = raw + correction), its decay clock, and the newest sample
    // the previous frame's STARVATION branch extrapolated from. A re-base is the discrete event where the newest
    // sample CHANGES while the render is in the starvation (extrapolate) regime — the only regime whose raw output
    // depends on the newest sample, so the only place an arrival can step the render. The captured jump is computed
    // SAME-INSTANT: (old basis extrapolated to this frame's playout time) − (new basis at the same time) — never
    // against the previous frame's rendered position, which would bake one frame of real motion into the offset
    // and turn the smoothing into per-sample lag (the first-cut bug this harness caught immediately).
    private double _correctionX;
    private double _correctionY;
    private TimeSpan? _lastSampleAt;
    private BufferedPosition? _lastStarvationBasis;

    private double _interpolationDelayMs;

    // The last position we rendered (returned by Sample). Held across frames so a starvation HOLD and the
    // catch-up glide both continue smoothly from the live render position rather than snapping.
    private RenderPosition _renderPosition;

    // The spawn / last-Reset anchor — the position Sample HOLDS on while the playout buffer has no real confirm to
    // render against (before the first Confirm, and immediately after a Reset). Kept SEPARATE from the playout
    // buffer (rather than seeded as a fake sample with a bogus timestamp) so the first REAL confirm doesn't lerp
    // across an astronomical synthetic span — the very first real bracket [from, to] is two genuine confirms.
    private RenderPosition _anchor;

    // MOVEMENT-ACTIONS (finding #1 fix): the REPLICATED airborne height (WorldEntity.VerticalOffset), lerped on the
    // SAME playout timeline + brackets + alpha as the horizontal position. This is the REAL server-authoritative
    // jump height (Phase C retired the cosmetic monster hop-arc factor that used to live here — a slime's hop is now a
    // real replicated Z that rides THIS field): a remote viewer must see another entity's jump
    // height and XY on ONE timeline, or the height leads/stair-steps while the horizontal glides (the un-buffered
    // raw-snapshot Z that this replaces). HOLDs on the bracketing sample's height in every HOLD regime, lerps in a
    // bracket; 0 on the spawn/Reset anchor (a freshly-spawned / AOI-re-entered entity is treated as grounded until
    // its first confirm ages in). Read via SampledVerticalOffset after Sample.
    private double _sampledVerticalOffset;

    public RemotePositionInterpolator(WorldVector initialPosition, double interpolationDelayMs)
    {
        _renderPosition = _anchor = RenderPosition.FromWorld(initialPosition);
        _interpolationDelayMs = Math.Max(0, interpolationDelayMs);
    }

    public RenderPosition RenderPosition => _renderPosition;

    public double InterpolationDelayMs => _interpolationDelayMs;

    // MOVEMENT-ACTIONS (finding #1 fix): the replicated airborne height for THIS frame's playout time, lerped on the
    // SAME timeline as the horizontal Sample — so a remote jump's height and XY share one clock (no lead / stair-step
    // vs the smooth glide). Read AFTER Sample. The caller uses this for remote entities instead of the raw latest
    // snapshot height — including a slime's hop, now a real replicated Z (Phase C retired the cosmetic bounce factor).
    public double SampledVerticalOffset => _sampledVerticalOffset;

    // Count of buffered samples — exposed for diagnostics (the trace's queue-depth read-out) and tests.
    public int BufferedSampleCount => _samples.Count;

    // Live-update the playout-buffer delay (the F1 "Remote interp buffer" knob, or a per-entity cadence retune).
    // Re-times the playout WITHOUT a discontinuity: Sample reads the new delay on the next call and the render
    // continues from its current position, so raising/lowering the buffer slides the playout cursor smoothly
    // rather than snapping (the live-knob no-discontinuity requirement).
    public void UpdateDelay(double interpolationDelayMs)
    {
        _interpolationDelayMs = Math.Max(0, interpolationDelayMs);
    }

    // Hard-reset onto a position (respawn / AOI re-entry / teleport): clear the playout buffer and snap the render
    // + the hold-anchor there. The next Sample holds on this anchor until fresh confirms refill the buffer (no
    // stale glide across the old path, no default pop). The anchor is treated as grounded (height 0) until a real
    // confirm ages in — an entity re-entering AOI mid-jump shows its true height within the buffer delay.
    public void Reset(WorldVector position)
    {
        _samples.Clear();
        _renderPosition = _anchor = RenderPosition.FromWorld(position);
        _sampledVerticalOffset = 0d;
        // A Reset is an intentional hard snap — never blend across it.
        _correctionX = _correctionY = 0d;
        _lastStarvationBasis = null;
        _lastSampleAt = null;
    }

    // A new server-confirmed continuous position (+ its replicated airborne height) arrived. Append it to the
    // playout buffer keyed on its arrival clock. OUT-OF-ORDER guard: ignore a sample whose arrival is not strictly
    // after the newest buffered one (unreliable transport can reorder/duplicate) — re-inserting an older sample
    // would let the playout lerp backward and rubberband. A repeated identical arrival time is likewise ignored.
    // `verticalOffset` defaults to 0 (grounded) so the XY-only callers/tests that predate the replicated height
    // remain correct (a grounded entity).
    // REMOTE-WALK Phase 1 (v39): `velocity` (units/sec, server-replicated) is BUFFERED on the sample for Phase 2's
    // dead-reckoning. It defaults to Zero so the callers/tests that predate it remain correct. Phase 1 ONLY stores it
    // — Sample does NOT extrapolate from it yet (a deliberate behavioral no-op; Phase 2 wires the extrapolation).
    public void Confirm(WorldVector position, TimeSpan receivedAt, double verticalOffset = 0d, WorldVector velocity = default)
    {
        if (_samples.Count > 0 && receivedAt <= _samples[^1].ReceivedAt)
        {
            return;
        }

        // BOSS-1 teleport snap: a jump beyond TeleportSnapUnits from the last known position is a server teleport,
        // not motion — Reset onto it (clear the buffer + snap the render/anchor) rather than sliding to it. Compare
        // against the newest buffered sample, or the live render position when the buffer is empty (Reset leaves
        // _renderPosition ON the snapped position, so a subsequent normal confirm from the destination won't re-trip
        // this). No buffer to lerp after a Reset ⇒ Sample HOLDs on the snapped anchor until fresh confirms age in.
        var reference = _samples.Count > 0 ? _samples[^1].Position : _renderPosition;
        var jumpX = position.X - reference.X;
        var jumpY = position.Y - reference.Y;
        if (((jumpX * jumpX) + (jumpY * jumpY)) > TeleportSnapUnits * TeleportSnapUnits)
        {
            Reset(position);
            return;
        }

        _samples.Add(new BufferedPosition(RenderPosition.FromWorld(position), verticalOffset, velocity, receivedAt));
        FastForwardIfBackedUp();

        while (_samples.Count > MaxBufferedSamples)
        {
            _samples.RemoveAt(0);
        }
    }

    // Catch-up cap (ported, time-domain): collapse a backed-up buffer so the render never trails the newest
    // received sample by more than ~CatchUpSampleCap samples. Drop the OLDEST samples — those are stale waypoints
    // the playout would otherwise crawl through one by one — but KEEP a short tail (>= 2 samples) so a final glide
    // remains (no hard teleport): Sample still lerps the current render position toward the newest over the kept
    // tail's span instead of snapping onto it. We never need to back-date timestamps the way the tile path did:
    // the time-domain playout already reads now - delay against real arrival clocks, so trimming the OLD end alone
    // brings the playout cursor inside the kept tail and the glide resumes from the live render position.
    private void FastForwardIfBackedUp()
    {
        if (_samples.Count <= CatchUpSampleCap)
        {
            return;
        }

        // Keep the newest CatchUpSampleCap samples (the tail we glide through); drop everything older. Keeping at
        // least 2 guarantees Sample has a bracket to lerp across rather than a single point to snap to.
        var dropCount = _samples.Count - CatchUpSampleCap;
        _samples.RemoveRange(0, dropCount);
    }

    // Render at playoutTime = now - InterpolationDelay by LERPING continuously between the two buffered samples
    // that bracket that playout time (float positions — no cadence quantization, so a tile-stepped source glides
    // strictly BETWEEN its tiles). Regimes:
    //   * no real confirms yet (or none after a Reset): HOLD on the spawn/Reset anchor.
    //   * one real confirm, or playoutTime before the oldest confirm (the buffer hasn't aged in yet): HOLD on the
    //     oldest confirm.
    //   * playoutTime at/after the newest confirm (STARVATION — no future sample to interpolate toward): EXTRAPOLATE
    //     along the newest sample's replicated velocity (dead-reckoning), capped at MaxExtrapolationMs. A non-moving
    //     entity (Velocity 0) extrapolates to a HOLD — a dropped packet for a resting/tile-stepped entity never flings.
    //   * playoutTime between two confirms: lerp them by the fractional position in that span.
    // The replicated airborne height (_sampledVerticalOffset) tracks the horizontal in EVERY regime — same HOLD
    // sample, same bracket, same alpha — so a remote jump's height and XY are always on one timeline.
    // Pure-functional on the buffer + clock; mutates only the cached _renderPosition (so HOLD/catch-up continue
    // from the live render position). Prunes confirms strictly older than the bracket so the buffer stays bounded.
    public RenderPosition Sample(TimeSpan now)
    {
        // No real confirm has landed (pre-first-Confirm, or just after a Reset): hold on the anchor (grounded).
        // No correction across the anchor regime — a fresh spawn must not inherit a stale blend.
        if (_samples.Count == 0)
        {
            _correctionX = _correctionY = 0d;
            _lastStarvationBasis = null;
            _lastSampleAt = now;
            _renderPosition = _anchor;
            _sampledVerticalOffset = 0d;
            return _renderPosition;
        }

        var playoutTime = now - TimeSpan.FromMilliseconds(_interpolationDelayMs);
        var starvation = false;
        RenderPosition raw;

        // Before the buffer has aged in (playout still behind the oldest confirm): hold on the oldest confirm.
        // Also the single-confirm case (oldest == newest) lands here or in the starvation branch — both HOLD.
        if (playoutTime <= _samples[0].ReceivedAt)
        {
            raw = _samples[0].Position;
            _sampledVerticalOffset = _samples[0].VerticalOffset;
        }

        // STARVATION: playout has caught up to (or passed) the newest confirm — nothing future to lerp toward.
        // REMOTE-WALK Phase 2 (v39 dead-reckoning): instead of HOLDing (which froze a remote walker between the sparse
        // tile-crossing samples → the "hold-then-rush" choppiness), EXTRAPOLATE along the newest sample's replicated
        // velocity — pos = newest.Position + newest.Velocity × elapsed — so the gap fills with continuous motion. A
        // resting / tile-stepped entity (Velocity == 0) extrapolates to a HOLD, identical to before (no fling on a
        // dropped packet for a non-moving entity). The elapsed is CAPPED at MaxExtrapolationMs so a moving entity that
        // goes silent (disconnect mid-stride, a dropped stop confirm) glides at most that far then holds, rather than
        // flinging away forever; a real stop normally arrives within the playout delay (its StateRevision stop-edge
        // bump) and lands the bracket lerp on the stop point before this branch even extrapolates past it. Height does
        // NOT extrapolate (a jump is force-included densely; a walker's height is 0) — hold the newest.
        else if (playoutTime >= _samples[^1].ReceivedAt)
        {
            var newest = _samples[^1];
            starvation = true;
            raw = ExtrapolateAt(newest, playoutTime);
            _sampledVerticalOffset = newest.VerticalOffset;
        }
        else
        {
            // Find the bracket [i, i+1] with samples[i].ReceivedAt <= playoutTime < samples[i+1].ReceivedAt.
            var index = 0;
            for (var i = 0; i < _samples.Count - 1; i++)
            {
                if (_samples[i + 1].ReceivedAt > playoutTime)
                {
                    index = i;
                    break;
                }
            }

            var from = _samples[index];
            var to = _samples[index + 1];
            var spanMs = (to.ReceivedAt - from.ReceivedAt).TotalMilliseconds;
            var alpha = spanMs > 0d ? (playoutTime - from.ReceivedAt).TotalMilliseconds / spanMs : 1d;
            raw = RenderPosition.Lerp(from.Position, to.Position, alpha);

            // MOVEMENT-ACTIONS (finding #1 fix): lerp the replicated height across the SAME bracket with the SAME alpha
            // the horizontal uses, so the remote jump's apex sits over the XY arc midpoint and the rise/fall tracks the
            // glide exactly (one timeline). No collision on Z, so a plain linear lerp of the two confirmed heights.
            _sampledVerticalOffset = from.VerticalOffset + ((to.VerticalOffset - from.VerticalOffset) * alpha);

            // Prune samples strictly older than the active bracket's start — the playout cursor has moved past them
            // and they will never be read again. Keeps the buffer at ~the steady-state depth without unbounded growth.
            if (index > 0)
            {
                _samples.RemoveRange(0, index);
            }
        }

        _renderPosition = ApplyCorrectionSmoothing(raw, now, playoutTime, starvation);
        return _renderPosition;
    }

    // The starvation-branch dead-reckoning formula, factored so the correction smoothing can evaluate what the
    // PREVIOUS basis would have rendered at the same playout instant (the same MaxExtrapolationMs clamp — a basis
    // already parked at the clamp projects to where it was parked).
    private static RenderPosition ExtrapolateAt(in BufferedPosition basis, TimeSpan playoutTime)
    {
        var elapsedSeconds =
            Math.Min((playoutTime - basis.ReceivedAt).TotalMilliseconds, MaxExtrapolationMs) / 1000d;
        return new RenderPosition(
            basis.Position.X + (basis.Velocity.X * elapsedSeconds),
            basis.Position.Y + (basis.Velocity.Y * elapsedSeconds));
    }

    // CORRECTION SMOOTHING (S-remote-render-jitter-200-clients): render = raw + a decaying offset. The offset
    // captures each re-base jump SAME-INSTANT — (old newest sample extrapolated to this playout time) − (new
    // newest at the same time) — added on top of whatever prior offset is still decaying, then decays with a
    // ~40ms half-life. Only the STARVATION regime re-bases (its raw depends on the newest sample; the bracket-lerp
    // raw is continuous across arrivals), so the capture is gated on starvation-to-starvation newest changes.
    // The offset's own drift velocity (|offset| × ln2 / half-life) stays well under walk speed for normal
    // per-sample errors, so the frame velocity never flips >90° — the shimmer renders as a smooth curve while the
    // BASE still tracks the newest data with zero added latency. An accumulated offset beyond MaxCorrectionUnits
    // is a teleport-scale discontinuity: snap (offset 0), exactly the pre-fix behavior. Height is never smoothed.
    private RenderPosition ApplyCorrectionSmoothing(RenderPosition raw, TimeSpan now, TimeSpan playoutTime, bool starvation)
    {
        // Decay whatever offset is outstanding over the elapsed frame time (before adding any new jump).
        if (_lastSampleAt is { } lastAt && (_correctionX != 0d || _correctionY != 0d))
        {
            var dtMs = (now - lastAt).TotalMilliseconds;
            if (dtMs > 0d)
            {
                var factor = Math.Pow(0.5, dtMs / CorrectionHalfLifeMs);
                _correctionX *= factor;
                _correctionY *= factor;
                if (Math.Abs(_correctionX) < 1e-4 && Math.Abs(_correctionY) < 1e-4)
                {
                    _correctionX = _correctionY = 0d;
                }
            }
        }

        if (starvation)
        {
            var newest = _samples[^1];
            if (_lastStarvationBasis is { } oldBasis && oldBasis.ReceivedAt != newest.ReceivedAt)
            {
                // Re-base: add the same-instant jump between the two bases so this frame renders continuous with
                // the trajectory the old basis was drawing. Chained onto the decayed prior offset.
                var oldRaw = ExtrapolateAt(oldBasis, playoutTime);
                _correctionX += oldRaw.X - raw.X;
                _correctionY += oldRaw.Y - raw.Y;
                if (((_correctionX * _correctionX) + (_correctionY * _correctionY)) > MaxCorrectionUnits * MaxCorrectionUnits)
                {
                    _correctionX = _correctionY = 0d;
                }
            }

            _lastStarvationBasis = newest;
        }
        else
        {
            // Bracket/hold regimes: raw is continuous across arrivals — nothing to capture; a later return to
            // starvation starts fresh from that regime's basis.
            _lastStarvationBasis = null;
        }

        _lastSampleAt = now;
        return new RenderPosition(raw.X + _correctionX, raw.Y + _correctionY);
    }

    // REMOTE-WALK Phase 1 (v39): Velocity (units/sec) is BUFFERED here per sample for Phase 2 dead-reckoning. Sample
    // does not yet read it (no extrapolation this phase) — it rides the sample purely so Phase 2 can turn on.
    private readonly record struct BufferedPosition(RenderPosition Position, double VerticalOffset, WorldVector Velocity, TimeSpan ReceivedAt);
}
