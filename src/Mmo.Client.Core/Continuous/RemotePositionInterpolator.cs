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

    // The playout buffer of received confirms, oldest-first: (continuous position, arrival clock) pairs. Sample(now)
    // renders at now - InterpolationDelay by lerping the two buffered confirms that bracket that playout time.
    private readonly List<BufferedPosition> _samples = new();

    // Hard cap on buffered samples so a long stall (server silent, render starved) can't grow the buffer
    // unbounded. Generous — far more than the steady-state depth (~InterpolationDelay / snapshot-interval, a
    // handful) so it never clips a legitimate window; the CatchUpSampleCap collapses genuine backlog first.
    private const int MaxBufferedSamples = 256;

    private double _interpolationDelayMs;

    // The last position we rendered (returned by Sample). Held across frames so a starvation HOLD and the
    // catch-up glide both continue smoothly from the live render position rather than snapping.
    private RenderPosition _renderPosition;

    // The spawn / last-Reset anchor — the position Sample HOLDS on while the playout buffer has no real confirm to
    // render against (before the first Confirm, and immediately after a Reset). Kept SEPARATE from the playout
    // buffer (rather than seeded as a fake sample with a bogus timestamp) so the first REAL confirm doesn't lerp
    // across an astronomical synthetic span — the very first real bracket [from, to] is two genuine confirms.
    private RenderPosition _anchor;

    // HOP-ARC (cosmetic): the [0,1] PARABOLIC factor of the bracket the playout is currently lerping across —
    // 4*alpha*(1-alpha): 0 at the bracket's start/end, 1 at its midpoint. Updated every Sample alongside the
    // horizontal lerp from the SAME alpha, so any caller that wants a vertical "jump" arc (the slime hop) gets a
    // height SYNCED to the horizontal move for free: peak * HopArcFactor rises and lands exactly as the position
    // does. 0 whenever the render is HOLDing (pre-confirm, before age-in, or starvation) or the active bracket
    // doesn't actually move (a repeated identical confirm) — so a RESTING monster never bounces. The interpolator
    // stays kind-agnostic: it only EXPOSES the factor; the caller gates it to EntityKind.Monster and scales by peak.
    private double _hopArcFactor;

    public RemotePositionInterpolator(WorldVector initialPosition, double interpolationDelayMs)
    {
        _renderPosition = _anchor = RenderPosition.FromWorld(initialPosition);
        _interpolationDelayMs = Math.Max(0, interpolationDelayMs);
    }

    public RenderPosition RenderPosition => _renderPosition;

    public double InterpolationDelayMs => _interpolationDelayMs;

    // HOP-ARC (cosmetic): the [0,1] parabolic factor (4*alpha*(1-alpha)) of the bracket the playout is currently
    // lerping across — 0 at the bracket ends and while HOLDing, 1 at the midpoint. A caller multiplies this by a
    // peak height to get a vertical jump arc SYNCED to the horizontal move. See the field comment. Read after Sample.
    public double HopArcFactor => _hopArcFactor;

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
    // stale glide across the old path, no default pop).
    public void Reset(WorldVector position)
    {
        _samples.Clear();
        _renderPosition = _anchor = RenderPosition.FromWorld(position);
        _hopArcFactor = 0d;
    }

    // A new server-confirmed continuous position arrived. Append it to the playout buffer keyed on its arrival
    // clock. OUT-OF-ORDER guard: ignore a sample whose arrival is not strictly after the newest buffered one
    // (unreliable transport can reorder/duplicate) — re-inserting an older sample would let the playout lerp
    // backward and rubberband. A repeated identical arrival time is likewise ignored.
    public void Confirm(WorldVector position, TimeSpan receivedAt)
    {
        if (_samples.Count > 0 && receivedAt <= _samples[^1].ReceivedAt)
        {
            return;
        }

        _samples.Add(new BufferedPosition(RenderPosition.FromWorld(position), receivedAt));
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
    //   * playoutTime at/after the newest confirm (STARVATION — no future sample to interpolate toward): HOLD on
    //     the newest. NO extrapolation (the deferred path) — the render parks on the last known position and waits
    //     for the next Confirm, so a dropped/late packet never flings the entity forward.
    //   * playoutTime between two confirms: lerp them by the fractional position in that span.
    // Pure-functional on the buffer + clock; mutates only the cached _renderPosition (so HOLD/catch-up continue
    // from the live render position). Prunes confirms strictly older than the bracket so the buffer stays bounded.
    public RenderPosition Sample(TimeSpan now)
    {
        // No real confirm has landed (pre-first-Confirm, or just after a Reset): hold on the anchor.
        if (_samples.Count == 0)
        {
            _renderPosition = _anchor;
            _hopArcFactor = 0d;
            return _renderPosition;
        }

        var playoutTime = now - TimeSpan.FromMilliseconds(_interpolationDelayMs);

        // Before the buffer has aged in (playout still behind the oldest confirm): hold on the oldest confirm.
        // Also the single-confirm case (oldest == newest) lands here or in the starvation branch — both HOLD.
        if (playoutTime <= _samples[0].ReceivedAt)
        {
            _renderPosition = _samples[0].Position;
            _hopArcFactor = 0d;
            return _renderPosition;
        }

        // STARVATION: playout has caught up to (or passed) the newest confirm — nothing future to lerp toward.
        // HOLD at the newest (no extrapolation). This is the dropped/late-packet case: park, don't fling.
        if (playoutTime >= _samples[^1].ReceivedAt)
        {
            _renderPosition = _samples[^1].Position;
            _hopArcFactor = 0d;
            return _renderPosition;
        }

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
        _renderPosition = RenderPosition.Lerp(from.Position, to.Position, alpha);

        // HOP-ARC (cosmetic): the parabolic factor for THIS bracket, from the SAME alpha the horizontal lerp uses,
        // so a caller's vertical jump (peak * factor) rises at the bracket midpoint and lands exactly when/where the
        // horizontal does — no separate timeline. 0 if the bracket doesn't actually move (a repeated identical
        // confirm: from == to) so a RESTING monster getting re-confirmed on its tile never bounces in place.
        var clampedAlpha = Math.Clamp(alpha, 0d, 1d);
        _hopArcFactor = from.Position == to.Position ? 0d : 4d * clampedAlpha * (1d - clampedAlpha);

        // Prune samples strictly older than the active bracket's start — the playout cursor has moved past them
        // and they will never be read again. Keeps the buffer at ~the steady-state depth without unbounded growth.
        if (index > 0)
        {
            _samples.RemoveRange(0, index);
        }

        return _renderPosition;
    }

    private readonly record struct BufferedPosition(RenderPosition Position, TimeSpan ReceivedAt);
}
