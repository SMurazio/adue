using Mmo.Shared.Domain;

namespace Mmo.Client.Core;

// S56 mouse hold-to-walk-toward-cursor (UO control). Maps the world-space tile delta from the player to the
// cursor tile onto the NEAREST of the 8 movement directions — the heading the avatar should hold while the
// right mouse button is down, re-aimed live every frame off the predicted position.
//
// Unlike ClickMoveController.HeadingToward (which signs each axis, so a shallow 10-east/1-south delta reads as
// SE), this picks the nearest of 8 angular sectors: a mostly-east cursor walks E, a true diagonal walks the
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
