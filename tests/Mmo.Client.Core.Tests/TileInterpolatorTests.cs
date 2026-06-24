using Mmo.Shared.Domain;
using Xunit;

namespace Mmo.Client.Core.Tests;

public sealed class TileInterpolatorTests
{
    [Fact]
    public void EffectiveCadenceMatchesServerTickQuantization()
    {
        Assert.Equal(150d, MovementCadence.EffectiveStepCadenceMs(140, 20));
        Assert.Equal(200d, MovementCadence.EffectiveStepCadenceMs(200, 20));
    }

    [Fact]
    public void LocalConfirmedStepStartsWithoutBuffer()
    {
        var interpolator = new TileInterpolator(new TileCoord(0, 0), 150, 0);

        interpolator.Confirm(new TileCoord(1, 0), TimeSpan.Zero);
        var halfway = interpolator.Sample(TimeSpan.FromMilliseconds(75));

        Assert.InRange(halfway.X, 0.49, 0.51);
        Assert.Equal(0, halfway.Y);
    }

    [Fact]
    public void RemoteJitteredStepsStayMonotonicWithoutBoundaryStall()
    {
        // remote-interp-tighten Part A: the remote jitter buffer was trimmed from 1.3*cadence (=195ms) to the new
        // default max(0.5*cadence, 50ms) = 75ms at the 150ms default cadence. Use 75 here (was 195): jittered
        // arrivals still glide tile-to-tile monotonically with no boundary stall, just with less playout lag.
        // Confirms are interleaved with sampling (as in the live client — one snapshot, then frames, then the next)
        // so this exercises the JITTER-absorption path, not the Part-B catch-up cap (which a 3-at-once burst trips).
        var interpolator = new TileInterpolator(new TileCoord(0, 0), 150, 75);

        var arrivals = new[] { 0, 140, 290 };
        var nextArrival = 0;
        var previous = double.NegativeInfinity;
        var positions = new Dictionary<int, RenderPosition>();
        for (var ms = 0; ms <= 700; ms += 25)
        {
            // Deliver each jittered confirm at its arrival time, before the frame at that ms (one tile at a time).
            if (nextArrival < arrivals.Length && ms >= arrivals[nextArrival])
            {
                interpolator.Confirm(new TileCoord(nextArrival + 1, 0), TimeSpan.FromMilliseconds(arrivals[nextArrival]));
                nextArrival++;
            }

            var position = interpolator.Sample(TimeSpan.FromMilliseconds(ms));
            positions[ms] = position;
            Assert.True(position.X + 0.0001 >= previous, $"position moved backward at {ms}ms");
            previous = position.X;
        }

        // Mid-step motion across two windows (no stall): the render is moving, not frozen on a tile boundary.
        Assert.True(positions[150].X > positions[100].X, "no motion in 100->150ms window");
        Assert.True(positions[400].X > positions[350].X, "no motion in 350->400ms window");
        // Reaches the newest confirmed tile by 700ms (with the tighter buffer it arrives comfortably earlier).
        Assert.InRange(positions[700].X, 2.99, 3.0);
    }

    // remote-interp-tighten Part A: normal one-tile-at-a-time confirms at a steady cadence with the trimmed 75ms
    // buffer glide smoothly tile-to-tile and NEVER trip the catch-up cap (QueueDepth stays small) — no stutter/stall.
    [Fact]
    public void SteadyOneTileCadenceGlidesSmoothlyAndNeverTripsCap()
    {
        const double cadence = 150d;
        const double buffer = 75d;
        var interpolator = new TileInterpolator(new TileCoord(0, 0), cadence, buffer);

        var previous = -1d;
        // One confirm every ~cadence (the regular tile-step rate), sampling between arrivals.
        for (var step = 1; step <= 6; step++)
        {
            var arrival = TimeSpan.FromMilliseconds(step * cadence);
            interpolator.Confirm(new TileCoord(step, 0), arrival);
            // The cap is 2 tiles; steady one-at-a-time confirms must keep the backlog at or under it.
            Assert.True(interpolator.QueueDepth <= 2, $"queue backed up to {interpolator.QueueDepth} under steady cadence");

            // Sample across this step window: motion is forward and monotonic (smooth glide, no stall).
            for (var ms = 0; ms <= cadence; ms += 25)
            {
                var pos = interpolator.Sample(arrival + TimeSpan.FromMilliseconds(ms));
                Assert.True(pos.X + 0.0001 >= previous, "render moved backward under steady cadence");
                previous = pos.X;
            }
        }

        // After the last step settles, the render has reached the newest tile (no permanent lag pile-up).
        var settled = interpolator.Sample(TimeSpan.FromMilliseconds(6 * cadence + 400));
        Assert.InRange(settled.X, 5.99, 6.0);
    }

    // remote-interp-tighten Part B: a queue backed up far past the cap (a hitch/tab-out dumps many confirms faster
    // than the glide can drain) FAST-FORWARDS — the render stays within ~the cap of the newest confirmed tile rather
    // than crawling 8 tiles behind — and it is NOT a hard teleport (a short final glide remains).
    [Fact]
    public void BackedUpQueueFastForwardsWithinCapWithoutHardTeleport()
    {
        var interpolator = new TileInterpolator(new TileCoord(0, 0), 150, 75);

        // Dump 8 tiles all "arriving" at once (a hitch / tab-out backlog), far past the 2-tile cap.
        for (var x = 1; x <= 8; x++)
        {
            interpolator.Confirm(new TileCoord(x, 0), TimeSpan.FromMilliseconds(50));
        }

        // The catch-up cap collapses the backlog immediately on confirm: depth never exceeds the cap.
        Assert.True(interpolator.QueueDepth <= 2, $"queue not capped after backlog: depth={interpolator.QueueDepth}");

        // Sample forward: the render converges onto (or very near) the newest tile (8) within a step or two —
        // it can NEVER be left 5+ tiles behind. The first sample is past the buffer so the glide has started.
        var pos = interpolator.Sample(TimeSpan.FromMilliseconds(50 + 75));
        // Not a hard teleport on the first sample: still gliding (hasn't already snapped onto tile 8).
        Assert.True(pos.X < 8.0, "fast-forward hard-teleported instead of gliding");

        // Within ~2 step durations it has caught up to the newest confirmed tile (8) — bounded trailing.
        var caughtUp = interpolator.Sample(TimeSpan.FromMilliseconds(50 + 75 + 350));
        Assert.True(caughtUp.X >= 7.0, $"render still {8 - caughtUp.X:0.0} tiles behind after catch-up");
        Assert.InRange(caughtUp.X, 7.0, 8.0);
    }

    // remote-interp-tighten Part B: a normal stream of single confirms (one tile at a time, glide keeps up) NEVER
    // trips the fast-forward — no tile is dropped, every waypoint is rendered through.
    [Fact]
    public void NormalSingleConfirmsNeverDropTiles()
    {
        var interpolator = new TileInterpolator(new TileCoord(0, 0), 150, 75);

        // Confirm tile 1, fully glide to it (so the queue drains) before confirming tile 2, etc.
        for (var x = 1; x <= 4; x++)
        {
            interpolator.Confirm(new TileCoord(x, 0), TimeSpan.FromMilliseconds((x - 1) * 150));
            Assert.True(interpolator.QueueDepth <= 2, "single confirms should never back up past the cap");
            // Drain this tile fully before the next confirm.
            interpolator.Sample(TimeSpan.FromMilliseconds(((x - 1) * 150) + 75 + 150));
        }

        // Every tile was rendered through: the render ends exactly on tile 4 (none skipped).
        var end = interpolator.Sample(TimeSpan.FromMilliseconds(1000));
        Assert.InRange(end.X, 3.99, 4.0);
    }
}
