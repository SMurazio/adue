using Mmo.Shared.Domain;

namespace Mmo.Client.Core;

// S56 mouse hold-to-walk-toward-cursor (UO control). Maps the world-space tile delta from the player to the
// cursor tile onto the NEAREST of the 8 movement directions — the heading the avatar should hold while the
// right mouse button is down, re-aimed live every frame off the predicted position.
//
// Rather than signing each axis independently (which would read a shallow 10-east/1-south delta as SE), this
// picks the nearest of 8 angular sectors: a mostly-east cursor walks E, a true diagonal walks the
// diagonal. That matches "hold toward the cursor" — you walk the way you're pointing, not the way the sign of
// each axis happens to fall. Pure and allocation-free so it unit-tests headlessly across all 8 sectors plus
// the same-tile (no heading) case.
public static class CursorHeading
{
    // The Direction8 from `from` toward `to`, or null when they are the same tile (no heading — caller stops
    // or holds). World axes: +X = east, +Y = south (the tile grid's screen-down). The 8 sectors are 45° wide
    // and centred on each cardinal/diagonal, so a delta is rounded to the closest compass point.
    public static Direction8? FromTileDelta(TileCoord from, TileCoord to)
    {
        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        if (dx == 0 && dy == 0)
        {
            return null;
        }

        // atan2(dy, dx) is the angle in the world plane (0 = east, +90° = south because +Y is down). Snap to
        // the nearest of 8 sectors (45° each) by dividing by 45° and rounding, then index a compass table.
        var degrees = Math.Atan2(dy, dx) * (180.0 / Math.PI);
        var sector = (int)Math.Round(degrees / 45.0);
        // Round can land on -4..4; wrap into 0..7. (-180° and +180° both mean west.)
        sector = ((sector % 8) + 8) % 8;
        return SectorToDirection[sector];
    }

    // S64: the held heading from a CONTINUOUS world vector (player render position -> cursor hit point), with a
    // dead-zone and octant hysteresis so it stays stable. This replaces the S56 FromTileDelta path, which
    // quantised BOTH endpoints to integer tiles (origin = the integer predicted tile that jumps a tile per step
    // and shifts on reconcile; cursor = a rounded tile that flickers octant on tiny moves near the player).
    //
    // dx/dy are CONTINUOUS world-plane deltas (+X=east, +Y=south), NOT tile counts. `lastHeading` is the
    // currently-held octant (or null if none). Returns:
    //   * null (no heading -> caller stops) when the vector magnitude is within `deadZoneUnits` — the cursor
    //     sits on/near the player, so emit no octant rather than whipping a near-zero atan2 around. Returning
    //     null (not lastHeading) lets the player STOP the avatar by parking the cursor on it, matching the prior
    //     on-own-tile stop behaviour and avoiding overshoot oscillation. (This is the deliberate "stop" arm of
    //     the spec's "hold previous / stop"; flip to `lastHeading` for a keep-walking-until-released feel.)
    //   * otherwise the nearest-of-8 octant, BUT only switches away from `lastHeading` once the cursor crosses
    //     the octant boundary by more than `hysteresisDegrees` — within that margin the previous octant is held,
    //     killing the boundary flicker between two adjacent directions.
    //
    // Pure + allocation-free so the octant / dead-zone / hysteresis logic unit-tests headlessly.
    public static Direction8? FromWorldVector(
        double dx,
        double dy,
        Direction8? lastHeading,
        double deadZoneUnits,
        double hysteresisDegrees)
    {
        // Dead-zone: too close to the player to define a stable heading -> no heading (caller stops/holds-still).
        var magnitude = Math.Sqrt((dx * dx) + (dy * dy));
        if (magnitude < deadZoneUnits)
        {
            return null;
        }

        // Angle in the world plane (0 = east, +90° = south because +Y is down), normalised to [0, 360).
        var degrees = Math.Atan2(dy, dx) * (180.0 / Math.PI);
        if (degrees < 0)
        {
            degrees += 360.0;
        }

        var sector = ((int)Math.Round(degrees / 45.0) % 8 + 8) % 8;
        var nearest = SectorToDirection[sector];

        // Hysteresis: if we already hold an octant, only switch once the cursor is more than `hysteresisDegrees`
        // past the boundary into the new octant. Each octant centre is sector*45°; the boundary toward the
        // neighbour sits at ±22.5°. We require the angle to be `hysteresisDegrees` BEYOND that boundary (i.e.
        // within 22.5° - hysteresis of the new centre) before committing, so a cursor parked on the boundary
        // keeps the old heading instead of flickering.
        if (lastHeading is { } held && held != nearest)
        {
            var lastSector = DirectionToSector(held);
            var deltaToLast = AngularDistanceDegrees(degrees, lastSector * 45.0);
            // Hold the previous octant while still within (22.5° + hysteresis) of its centre — i.e. the cursor
            // has not yet moved a full half-sector PLUS the sticky margin away from where it was committed.
            if (deltaToLast <= 22.5 + hysteresisDegrees)
            {
                return held;
            }
        }

        return nearest;
    }

    // Smallest absolute angular distance (degrees) between two angles, accounting for wrap at 360°.
    private static double AngularDistanceDegrees(double a, double b)
    {
        var diff = Math.Abs(a - b) % 360.0;
        return diff > 180.0 ? 360.0 - diff : diff;
    }

    private static int DirectionToSector(Direction8 direction)
    {
        for (var i = 0; i < SectorToDirection.Length; i++)
        {
            if (SectorToDirection[i] == direction)
            {
                return i;
            }
        }

        return 0;
    }

    // sector 0 = east (0°), increasing clockwise in screen space (+Y down): 1=SE(45°), 2=S(90°), 3=SW(135°),
    // 4=W(180°), 5=NW(-135°/225°), 6=N(-90°/270°), 7=NE(-45°/315°).
    private static readonly Direction8[] SectorToDirection =
    [
        Direction8.E,
        Direction8.SE,
        Direction8.S,
        Direction8.SW,
        Direction8.W,
        Direction8.NW,
        Direction8.N,
        Direction8.NE,
    ];
}
