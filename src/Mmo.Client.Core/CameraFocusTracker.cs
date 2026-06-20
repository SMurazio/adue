namespace Mmo.Client.Core;

// S95: pure camera-focus math, extracted out of the Godot client so it is unit-testable (no Godot types).
// The Godot UpdateCamera blends the confirmed tile and the cosmetic render position, then temporally smooths
// a persistent focus point toward that blend. This struct holds the persistent focus and does the per-frame
// math; the Godot side only translates to/from Vector3 and applies the camera offset/LookAt.
//
// Frame-rate independence: the smoothing uses focus += (target-focus) * (1 - exp(-rate*delta)), which converges
// at the same wall-clock speed regardless of frame rate (unlike a raw per-frame lerp factor). rate == 0 disables
// smoothing (hard-set to target). A teleport guard snaps instantly when the target jumps farther than a
// threshold (respawn / zone change / big knockback) so the camera never glides across the map.
public struct CameraFocusTracker
{
    private double _focusX;
    private double _focusY;
    private bool _seeded;

    // Current smoothed focus point (world X/Y; Godot maps Y to its Z plane).
    public readonly double FocusX => _focusX;
    public readonly double FocusY => _focusY;
    public readonly bool Seeded => _seeded;

    // Blend the confirmed tile (tileX/tileY) and the cosmetic render position (cosmeticX/cosmeticY) by
    // followBlend in [0,1] (0 = confirmed tile only, 1 = cosmetic/character — today's behavior), then smooth
    // the persistent focus toward that blend using this frame's delta seconds and the per-second smoothing rate.
    //
    // Returns the new focus point. On the first call (or after a teleport beyond teleportSnapDistance tiles)
    // the focus is hard-snapped to the target — no glide from the origin / across the map.
    public (double X, double Y) Advance(
        double tileX,
        double tileY,
        double cosmeticX,
        double cosmeticY,
        double followBlend,
        double smoothingPerSecond,
        double deltaSeconds,
        double teleportSnapDistance)
    {
        var blend = Clamp01(followBlend);
        var targetX = tileX + (cosmeticX - tileX) * blend;
        var targetY = tileY + (cosmeticY - tileY) * blend;

        // First frame with a render state, or a jump beyond the teleport threshold: snap instantly.
        if (!_seeded || Distance(_focusX, _focusY, targetX, targetY) > teleportSnapDistance)
        {
            _focusX = targetX;
            _focusY = targetY;
            _seeded = true;
            return (_focusX, _focusY);
        }

        if (smoothingPerSecond <= 0d || deltaSeconds <= 0d)
        {
            // Smoothing off (or no time elapsed): hard-follow the target exactly like today's camera.
            _focusX = targetX;
            _focusY = targetY;
            return (_focusX, _focusY);
        }

        var t = 1d - System.Math.Exp(-smoothingPerSecond * deltaSeconds);
        _focusX += (targetX - _focusX) * t;
        _focusY += (targetY - _focusY) * t;
        return (_focusX, _focusY);
    }

    public void Reset()
    {
        _focusX = 0d;
        _focusY = 0d;
        _seeded = false;
    }

    private static double Clamp01(double v) => v < 0d ? 0d : (v > 1d ? 1d : v);

    private static double Distance(double ax, double ay, double bx, double by)
    {
        var dx = ax - bx;
        var dy = ay - by;
        return System.Math.Sqrt((dx * dx) + (dy * dy));
    }
}
