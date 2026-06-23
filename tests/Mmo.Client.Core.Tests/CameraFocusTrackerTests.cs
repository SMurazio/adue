using Mmo.Client.Core;
using Xunit;

namespace Mmo.Client.Core.Tests;

// S95 — unit tests for the pure camera-focus math (blend + frame-rate-independent smoothing + teleport snap),
// the Godot-free seam behind UpdateCamera. No Godot types: plain doubles, deterministic deltas.
public sealed class CameraFocusTrackerTests
{
    private const double SnapTiles = 4d;

    [Fact]
    public void FirstFrameSeedsToTargetInsteadOfGlidingFromOrigin()
    {
        var tracker = new CameraFocusTracker();
        // blend 1.0 (cosmetic), smoothing 10/s — but on the very first frame it must SNAP, not glide from (0,0).
        var (x, y) = tracker.Advance(50, 50, 50, 50, followBlend: 1.0, smoothingPerSecond: 10, deltaSeconds: 0.016, teleportSnapDistance: SnapTiles);
        Assert.Equal(50d, x, 6);
        Assert.Equal(50d, y, 6);
        Assert.True(tracker.Seeded);
    }

    [Fact]
    public void BlendOfZeroFollowsConfirmedTileOnly()
    {
        var tracker = new CameraFocusTracker();
        // blend 0 => ignore cosmetic entirely, focus on the confirmed tile (smoothing off for an exact assert).
        var (x, y) = tracker.Advance(10, 20, 12.5, 22.5, followBlend: 0.0, smoothingPerSecond: 0, deltaSeconds: 0.016, teleportSnapDistance: SnapTiles);
        Assert.Equal(10d, x, 6);
        Assert.Equal(20d, y, 6);
    }

    [Fact]
    public void BlendOfOneFollowsRenderedPositionExactlyLikeTodaysCamera()
    {
        var tracker = new CameraFocusTracker();
        var (x, y) = tracker.Advance(10, 20, 12.5, 22.5, followBlend: 1.0, smoothingPerSecond: 0, deltaSeconds: 0.016, teleportSnapDistance: SnapTiles);
        Assert.Equal(12.5d, x, 6);
        Assert.Equal(22.5d, y, 6);
    }

    [Fact]
    public void BlendOfHalfPicksTheMidpoint()
    {
        var tracker = new CameraFocusTracker();
        var (x, y) = tracker.Advance(10, 20, 14, 24, followBlend: 0.5, smoothingPerSecond: 0, deltaSeconds: 0.016, teleportSnapDistance: SnapTiles);
        Assert.Equal(12d, x, 6);
        Assert.Equal(22d, y, 6);
    }

    [Fact]
    public void SmoothingConvergesAtTheSameWallClockSpeedRegardlessOfFrameRate()
    {
        // Seed both trackers at the origin, then drive each toward the same target for the same total wall time
        // (1.0s) but at different frame rates. Frame-rate-independent smoothing must land on the same focus.
        const double rate = 8d;
        const double totalSeconds = 1.0d;

        // Isolate the smoothing math from the teleport guard: the target (10,10) is ~14 tiles from the seed at
        // the origin, which legitimately exceeds the normal SnapTiles threshold (that snap is for respawns/zone
        // changes). This test is about frame-rate-independent CONVERGENCE, so pass a large threshold so the guard
        // never fires and we measure pure smoothing.
        const double noSnap = 1000d;

        var coarse = new CameraFocusTracker();
        coarse.Advance(0, 0, 0, 0, 1.0, 0, 0, noSnap); // seed at origin
        var fine = new CameraFocusTracker();
        fine.Advance(0, 0, 0, 0, 1.0, 0, 0, noSnap);

        // 10 steps of 0.1s
        for (int i = 0; i < 10; i++)
        {
            coarse.Advance(0, 0, 10, 10, 1.0, rate, 0.1, noSnap);
        }
        // 100 steps of 0.01s — same total time.
        for (int i = 0; i < 100; i++)
        {
            fine.Advance(0, 0, 10, 10, 1.0, rate, 0.01, noSnap);
        }

        // Both should be very close to each other (frame-rate independence) and partway to the target (not snapped).
        Assert.Equal(coarse.FocusX, fine.FocusX, 3);
        Assert.Equal(coarse.FocusY, fine.FocusY, 3);
        var expected = 10d * (1d - System.Math.Exp(-rate * totalSeconds));
        Assert.Equal(expected, fine.FocusX, 3);
        Assert.True(fine.FocusX < 10d, "should not have reached the target yet");
    }

    [Fact]
    public void SmoothingDoesNotInstantlySnapWhenEnabled()
    {
        var tracker = new CameraFocusTracker();
        tracker.Advance(0, 0, 0, 0, 1.0, 0, 0, SnapTiles); // seed at origin
        var (x, _) = tracker.Advance(0, 0, 2, 0, 1.0, 10, 0.016, SnapTiles);
        Assert.True(x > 0d && x < 2d, "smoothing should move partway, not snap");
    }

    [Fact]
    public void TeleportBeyondThresholdSnapsInstantlyEvenWithSmoothingOn()
    {
        var tracker = new CameraFocusTracker();
        tracker.Advance(0, 0, 0, 0, 1.0, 0, 0, SnapTiles); // seed at origin
        // Target jumps 100 tiles away (respawn/zone change) with smoothing on — must snap, not glide.
        var (x, y) = tracker.Advance(100, 0, 100, 0, 1.0, 10, 0.016, SnapTiles);
        Assert.Equal(100d, x, 6);
        Assert.Equal(0d, y, 6);
    }

    [Fact]
    public void SmallMoveWithinThresholdSmoothsRatherThanSnaps()
    {
        var tracker = new CameraFocusTracker();
        tracker.Advance(0, 0, 0, 0, 1.0, 0, 0, SnapTiles); // seed at origin
        // 3-tile move (< 4 threshold) with smoothing on: should glide, landing strictly between 0 and 3.
        var (x, _) = tracker.Advance(3, 0, 3, 0, 1.0, 10, 0.016, SnapTiles);
        Assert.True(x > 0d && x < 3d);
    }

    [Fact]
    public void FollowBlendIsClampedToUnitRange()
    {
        var tracker = new CameraFocusTracker();
        // blend 5 clamps to 1 (cosmetic); blend -2 clamps to 0 (confirmed tile).
        var (x1, _) = tracker.Advance(10, 0, 20, 0, 5.0, 0, 0.016, SnapTiles);
        Assert.Equal(20d, x1, 6);

        var fresh = new CameraFocusTracker();
        var (x2, _) = fresh.Advance(10, 0, 20, 0, -2.0, 0, 0.016, SnapTiles);
        Assert.Equal(10d, x2, 6);
    }
}
