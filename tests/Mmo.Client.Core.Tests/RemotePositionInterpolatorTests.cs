using Mmo.Client.Core;
using Mmo.Client.Core.Continuous;
using Mmo.Shared.Domain;
using Xunit;

namespace Mmo.Client.Core.Tests;

// CONTINUOUS MIGRATION (Phase 5): the continuous remote render driver — a fixed-delay playout buffer that lerps
// between received positions so EVERY remote entity glides smoothly (continuous players along their path,
// tile-stepped monsters BETWEEN their tiles — the hop is gone). The float analog of the retired TileInterpolator.
// These tests assert: smooth continuous playout (~1 buffer behind, monotonic, no pops); smooth tile-stepped
// playout (render STRICTLY BETWEEN the two tiles — the glide replaces the pop); starvation HOLDs at the newest
// (no extrapolation fling); out-of-order ignored; the catch-up cap collapses a backed-up buffer with a final
// glide; lifecycle (first sample adopted, Reset snaps); a live buffer-knob change re-times without a discontinuity.
public sealed class RemotePositionInterpolatorTests
{
    private const double Delay = 75d;

    private static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);

    // CONTINUOUS source: positions arrive on a steady cadence at fractional coordinates. The render should glide
    // smoothly ~one buffer behind, monotonically increasing in X with no pops/reversals.
    [Fact]
    public void ContinuousSourceGlidesSmoothlyOneBufferBehind()
    {
        var interp = new RemotePositionInterpolator(new WorldVector(0, 0), Delay);

        // Steady arrivals every 50 ms along +X at 0.5 tiles per arrival (a continuous remote player walking).
        for (var i = 1; i <= 6; i++)
        {
            interp.Confirm(new WorldVector(i * 0.5, 0), Ms(i * 50));
        }

        var previousX = double.NegativeInfinity;
        // Sample across the buffered window (playout = now - 75ms): from when the first sample is eligible to the
        // last. Render must be strictly monotonic and lag behind the newest received position.
        for (var nowMs = 100d; nowMs <= 300d; nowMs += 10d)
        {
            var render = interp.Sample(Ms(nowMs));
            Assert.True(render.X >= previousX - 1e-9, $"X went backwards at now={nowMs}: {render.X} < {previousX}");
            // The render is behind the newest received X (3.0 by t=300) — playout buffer lag, never ahead.
            Assert.True(render.X <= 3.0 + 1e-9, $"render ran ahead of the newest sample at now={nowMs}: {render.X}");
            previousX = render.X;
        }

        // It actually MOVED (not stuck at the anchor) and is roughly one buffer (~75ms => ~0.75 tile at this
        // speed) behind the newest by the end of the window.
        Assert.True(previousX > 0.5, $"render barely advanced: {previousX}");
    }

    // TILE-STEPPED source (a monster: Velocity=0, the server snaps it tile-to-tile). The render must glide
    // STRICTLY BETWEEN the two tiles at the mid-playout — the pop/hop is gone, the slime glides.
    [Fact]
    public void TileSteppedSourceRendersStrictlyBetweenTiles()
    {
        var interp = new RemotePositionInterpolator(new WorldVector(5, 5), Delay);

        // The monster sat on (5,5), then the server snapped it to (6,5) one snapshot later (a tile step).
        interp.Confirm(new WorldVector(5, 5), Ms(0));
        interp.Confirm(new WorldVector(6, 5), Ms(50));

        // Playout = now - 75ms. At now = 100ms the playout time is 25ms — squarely between the two arrivals
        // (0ms and 50ms), so the render must be strictly BETWEEN tile 5 and tile 6, NOT snapped to either.
        var mid = interp.Sample(Ms(100));
        Assert.True(mid.X > 5.0 + 1e-6, $"render snapped to/under the old tile: {mid.X}");
        Assert.True(mid.X < 6.0 - 1e-6, $"render snapped to/over the new tile: {mid.X}");
        Assert.Equal(5.0, mid.Y, 6);
    }

    // MOVEMENT-ACTIONS (finding #1): the replicated airborne height (VerticalOffset) must ride the SAME playout
    // timeline as the horizontal — same bracket, same alpha — so a remote jump's height and XY share one clock (no
    // lead / stair-step vs the smooth glide). Lerps in a bracket; HOLDs the bracketing sample's height in the HOLD
    // regimes (it parks WITH the position, it does not pop to ground).
    [Fact]
    public void ReplicatedVerticalOffsetRidesTheSamePlayoutTimelineAsXY()
    {
        var interp = new RemotePositionInterpolator(new WorldVector(0, 0), Delay);

        // Two confirms 50 ms apart: XY moves 0->1 in X while the height rises 0.0 -> 2.0 (a jump-arc segment).
        interp.Confirm(new WorldVector(0, 0), Ms(0), verticalOffset: 0.0);
        interp.Confirm(new WorldVector(1, 0), Ms(50), verticalOffset: 2.0);

        // Playout = now - 75ms. At now=100 the playout time is 25ms — the bracket midpoint (alpha=0.5). The height
        // is the SAME-alpha lerp the horizontal uses: X≈0.5 AND VerticalOffset≈1.0 (both 50% through the bracket).
        var render = interp.Sample(Ms(100));
        Assert.Equal(0.5, render.X, 6);
        Assert.Equal(1.0, interp.SampledVerticalOffset, 6); // height tracks the SAME alpha as XY — one timeline

        // Pre-age-in (playout = -5ms, before the oldest confirm): HOLD the oldest height (grounded here).
        interp.Sample(Ms(70));
        Assert.Equal(0.0, interp.SampledVerticalOffset, 6);

        // Starvation (playout = 425ms, past the newest): HOLD the newest height (this segment's apex) WITH the
        // position — it does NOT pop to ground.
        var starved = interp.Sample(Ms(500));
        Assert.Equal(2.0, interp.SampledVerticalOffset, 6);
        Assert.Equal(1.0, starved.X, 6);
    }

    // A dropped / late packet (the buffer starves: playout passes the newest sample with nothing future to lerp
    // toward) must HOLD at the newest sample — NO extrapolation fling forward.
    [Fact]
    public void StarvationHoldsAtNewestNoFling()
    {
        var interp = new RemotePositionInterpolator(new WorldVector(0, 0), Delay);

        interp.Confirm(new WorldVector(1, 0), Ms(0));
        interp.Confirm(new WorldVector(2, 0), Ms(50));

        // The next packet was dropped — no Confirm after t=50. Advance well past it (playout = 425 >> 50).
        var held = interp.Sample(Ms(500));
        Assert.Equal(2.0, held.X, 6); // parked on the newest, did NOT fling toward 3,4,...
        Assert.Equal(0.0, held.Y, 6);

        // Still held a frame later — no creep.
        var stillHeld = interp.Sample(Ms(600));
        Assert.Equal(2.0, stillHeld.X, 6);
    }

    // An out-of-order arrival (a sample whose receivedAt is older than the newest buffered one) is ignored — it
    // must not let the playout lerp backward / rubberband.
    [Fact]
    public void OutOfOrderArrivalIgnored()
    {
        var interp = new RemotePositionInterpolator(new WorldVector(0, 0), Delay);

        interp.Confirm(new WorldVector(1, 0), Ms(0));
        interp.Confirm(new WorldVector(2, 0), Ms(50));
        var before = interp.BufferedSampleCount;

        // A reordered/duplicate arrival timestamped BEFORE the newest — must be dropped.
        interp.Confirm(new WorldVector(99, 99), Ms(40));
        Assert.Equal(before, interp.BufferedSampleCount);

        // And a duplicate at the exact newest time — also dropped.
        interp.Confirm(new WorldVector(99, 99), Ms(50));
        Assert.Equal(before, interp.BufferedSampleCount);

        // The playout never sees the bogus (99,99): it stays on the real path.
        var render = interp.Sample(Ms(100));
        Assert.True(render.X is > 1.0 and < 2.0, $"out-of-order corrupted the playout: {render.X}");
    }

    // A backed-up buffer (a burst of samples ahead of the playout cursor — a hitch/tab-out) is collapsed by the
    // catch-up cap: the render fast-forwards toward the newest with a short final glide, never trailing far behind.
    [Fact]
    public void CatchUpCapCollapsesBackedUpBuffer()
    {
        var interp = new RemotePositionInterpolator(new WorldVector(0, 0), Delay);

        // 20 samples land in a burst (the render was stalled / the clock didn't advance). The buffer must not
        // grow without bound, and the render must be able to reach near the newest sample shortly after.
        for (var i = 1; i <= 20; i++)
        {
            interp.Confirm(new WorldVector(i, 0), Ms(i * 50));
        }

        // The cap kept only a short tail (not all 20).
        Assert.True(interp.BufferedSampleCount < 20, $"buffer not collapsed: {interp.BufferedSampleCount}");

        // Advancing the clock to the end, the render reaches near the newest (20,0) — caught up, no permanent lag.
        var caughtUp = interp.Sample(Ms(20 * 50 + 200));
        Assert.True(caughtUp.X >= 19.0, $"render did not catch up to the newest: {caughtUp.X}");

        // ...and it was a GLIDE not a teleport: a frame partway through the kept tail is strictly between tiles.
        var glide = interp.Sample(Ms(20 * 50 + 25));
        Assert.True(glide.X is > 0.0 and < 20.0, $"catch-up was not a glide: {glide.X}");
    }

    // Lifecycle: the spawn position is adopted as the first sample (the very first Sample, before any Confirm,
    // holds on the anchor — no default pop). Reset snaps to a new position and clears the prior buffer.
    [Fact]
    public void FirstSampleAdoptedAndResetSnaps()
    {
        var interp = new RemotePositionInterpolator(new WorldVector(3, 7), Delay);

        // Before any Confirm: holds on the spawn anchor.
        var anchored = interp.Sample(Ms(100));
        Assert.Equal(3.0, anchored.X, 6);
        Assert.Equal(7.0, anchored.Y, 6);

        interp.Confirm(new WorldVector(4, 7), Ms(50));
        interp.Confirm(new WorldVector(5, 7), Ms(100));

        // Reset (respawn / AOI re-entry) snaps the render to the new anchor and drops the old path.
        interp.Reset(new WorldVector(50, 50));
        var snapped = interp.Sample(Ms(200));
        Assert.Equal(50.0, snapped.X, 6);
        Assert.Equal(50.0, snapped.Y, 6);
    }

    // A live buffer-knob change (UpdateDelay — the F1 "Remote interp buffer" knob) re-times the playout WITHOUT a
    // discontinuity: the render position right after the change is close to the position right before it (the
    // playout cursor slides, it doesn't jump).
    [Fact]
    public void LiveBufferKnobRetimesWithoutDiscontinuity()
    {
        var interp = new RemotePositionInterpolator(new WorldVector(0, 0), Delay);

        for (var i = 1; i <= 8; i++)
        {
            interp.Confirm(new WorldVector(i * 0.5, 0), Ms(i * 50));
        }

        // Settle the playout mid-window.
        var beforeKnob = interp.Sample(Ms(250));

        // Raise the buffer from 75ms to 150ms live (the knob). The playout cursor moves further into the past, so
        // the render shifts — but smoothly, by an amount bounded by the extra buffer's worth of motion, NOT a snap
        // to a default / to the anchor.
        interp.UpdateDelay(150d);
        var afterKnob = interp.Sample(Ms(250));

        // ~75ms of extra buffer at 0.5 tiles / 50ms ≈ 0.75 tile of shift — bounded and continuous, not a jump to 0.
        Assert.True(Math.Abs(afterKnob.X - beforeKnob.X) < 1.0, $"knob change caused a discontinuity: {beforeKnob.X} -> {afterKnob.X}");
        Assert.True(afterKnob.X < beforeKnob.X, "raising the buffer should move the playout further into the past (smaller X)");
    }

    // The delay floor is honored (a negative delay clamps to 0) — defensive, mirrors the tile interpolator.
    [Fact]
    public void NegativeDelayClampsToZero()
    {
        var interp = new RemotePositionInterpolator(new WorldVector(0, 0), -50d);
        Assert.Equal(0d, interp.InterpolationDelayMs);
    }
}
