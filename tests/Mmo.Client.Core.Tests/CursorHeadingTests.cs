using Mmo.Client.Core;
using Mmo.Shared.Domain;
using Xunit;

namespace Mmo.Client.Core.Tests;

// Mouse hold-to-walk-toward-cursor (S64 CONTINUOUS): CursorHeading.FromWorldVector maps the float player->cursor
// world vector to the nearest of 8 octants, with a dead-zone (stop when the cursor is on/near the player) and
// octant hysteresis (no flicker at a boundary). +X = east, +Y = south (screen-down). (The older tile-delta
// heading FromTileDelta was removed as dead code — the continuous path superseded it.)
public sealed class CursorHeadingTests
{
    // ---- S64: FromWorldVector (continuous player->cursor vector + dead-zone + octant hysteresis) ----------

    private const double DeadZone = 0.6;
    private const double Hysteresis = 6.0;

    [Theory]
    // Continuous world deltas (+X=east, +Y=south), well outside the dead-zone, with no held heading: each
    // quadrant/axis rounds to its nearest octant exactly like the tile path, but from FLOAT deltas.
    [InlineData(3.0, -0.1, Direction8.E)]
    [InlineData(2.4, -2.6, Direction8.NE)]
    [InlineData(0.1, -3.0, Direction8.N)]
    [InlineData(-2.6, -2.4, Direction8.NW)]
    [InlineData(-3.0, 0.1, Direction8.W)]
    [InlineData(-2.4, 2.6, Direction8.SW)]
    [InlineData(-0.1, 3.0, Direction8.S)]
    [InlineData(2.6, 2.4, Direction8.SE)]
    public void FromWorldVector_OutsideDeadZone_MapsToNearestOctant(double dx, double dy, Direction8 expected)
    {
        Assert.Equal(expected, CursorHeading.FromWorldVector(dx, dy, lastHeading: null, DeadZone, Hysteresis));
    }

    [Theory]
    // Inside the dead-zone (|v| < 0.6 tile): no heading, regardless of any previously-held octant. The avatar
    // stops when the cursor sits on/near it instead of whipping a near-zero atan2 around.
    [InlineData(0.0, 0.0)]
    [InlineData(0.3, 0.3)]   // |v| ~= 0.42 < 0.6
    [InlineData(-0.4, 0.2)]  // |v| ~= 0.45 < 0.6
    public void FromWorldVector_InsideDeadZone_ReturnsNull(double dx, double dy)
    {
        Assert.Null(CursorHeading.FromWorldVector(dx, dy, lastHeading: null, DeadZone, Hysteresis));
        // Even while already holding a heading, the dead-zone yields null (stop), not the held octant.
        Assert.Null(CursorHeading.FromWorldVector(dx, dy, Direction8.E, DeadZone, Hysteresis));
    }

    [Fact]
    public void FromWorldVector_NoHeldHeading_NoHysteresis_PicksNearestAtBoundary()
    {
        // Angle ~26° (east-of-SE boundary at 22.5°): nearest is SE. With no prior heading there is nothing to
        // stick to, so it commits to SE immediately.
        var dx = Math.Cos(26.0 * Math.PI / 180.0) * 3.0;
        var dy = Math.Sin(26.0 * Math.PI / 180.0) * 3.0;
        Assert.Equal(Direction8.SE, CursorHeading.FromWorldVector(dx, dy, lastHeading: null, DeadZone, Hysteresis));
    }

    [Fact]
    public void FromWorldVector_Hysteresis_HoldsPreviousOctantInsideMargin()
    {
        // Cursor just past the E|SE boundary (24° — only 1.5° into the SE sector). Held heading = E. Within the
        // 22.5°+6° = 28.5° stickiness window from E's centre (0°), so the heading STAYS E — no boundary flicker.
        var dx = Math.Cos(24.0 * Math.PI / 180.0) * 3.0;
        var dy = Math.Sin(24.0 * Math.PI / 180.0) * 3.0;
        Assert.Equal(Direction8.E, CursorHeading.FromWorldVector(dx, dy, Direction8.E, DeadZone, Hysteresis));
    }

    [Fact]
    public void FromWorldVector_Hysteresis_SwitchesOncePastMargin()
    {
        // Cursor at 30° — beyond E's 28.5° stickiness window, so even while holding E it commits to SE.
        var dx = Math.Cos(30.0 * Math.PI / 180.0) * 3.0;
        var dy = Math.Sin(30.0 * Math.PI / 180.0) * 3.0;
        Assert.Equal(Direction8.SE, CursorHeading.FromWorldVector(dx, dy, Direction8.E, DeadZone, Hysteresis));
    }

    [Fact]
    public void FromWorldVector_Hysteresis_HoldsAcrossBoundaryBothDirections()
    {
        // Symmetric check on the other side of E's centre: cursor at -24° (just into the NE sector). Held = E,
        // within the window -> stays E.
        var dxNeg = Math.Cos(-24.0 * Math.PI / 180.0) * 3.0;
        var dyNeg = Math.Sin(-24.0 * Math.PI / 180.0) * 3.0;
        Assert.Equal(Direction8.E, CursorHeading.FromWorldVector(dxNeg, dyNeg, Direction8.E, DeadZone, Hysteresis));
    }

    [Fact]
    public void FromWorldVector_Hysteresis_HoldsAcrossWrapAtWest()
    {
        // West sits at 180°. A cursor at 156° (24° from W's centre, just into the SW sector) held as W stays W,
        // confirming the angular-distance wrap is handled away from 0°.
        var dx = Math.Cos(156.0 * Math.PI / 180.0) * 3.0;
        var dy = Math.Sin(156.0 * Math.PI / 180.0) * 3.0;
        Assert.Equal(Direction8.W, CursorHeading.FromWorldVector(dx, dy, Direction8.W, DeadZone, Hysteresis));
    }

    // ---- FREE-ANGLE A/B TEST: FreeAngleFromWorldVector (raw unit heading, dead-zone stop, NO octant snap) -------

    [Theory]
    // Inside the dead-zone (|v| < 0.6 tile): no heading (null) — the avatar STOPS, exactly like FromWorldVector.
    [InlineData(0.0, 0.0)]
    [InlineData(0.3, 0.3)]   // |v| ~= 0.42 < 0.6
    [InlineData(-0.4, 0.2)]  // |v| ~= 0.45 < 0.6
    public void FreeAngle_InsideDeadZone_ReturnsNull(double dx, double dy)
    {
        Assert.Null(CursorHeading.FreeAngleFromWorldVector(dx, dy, DeadZone));
    }

    [Theory]
    // Outside the dead-zone: the RAW normalized heading — length 1, same direction as (dx,dy), NOT snapped to an
    // octant. Each row is a plain axis/diagonal to check normalization; the off-octant angle is checked below.
    [InlineData(3.0, 0.0, 1.0, 0.0)]
    [InlineData(0.0, -3.0, 0.0, -1.0)]
    [InlineData(2.0, 2.0, 0.70710678, 0.70710678)]
    public void FreeAngle_OutsideDeadZone_ReturnsRawUnitVector(double dx, double dy, double ux, double uy)
    {
        var heading = CursorHeading.FreeAngleFromWorldVector(dx, dy, DeadZone);
        Assert.NotNull(heading);
        Assert.Equal(ux, heading!.Value.X, 6);
        Assert.Equal(uy, heading.Value.Y, 6);
        Assert.Equal(1.0, heading.Value.Length, 9); // unit length
    }

    [Fact]
    public void FreeAngle_OffOctantAngle_KeepsExactHeading_NotSnapped()
    {
        // A cursor at ~26° (past the 22.5° E|SE boundary). FromWorldVector SNAPS this to the SE octant; FreeAngle
        // keeps the EXACT 26° heading (the whole point of the A/B mode). Compare the two on the identical input.
        const double angleRad = 26.0 * Math.PI / 180.0;
        var dx = Math.Cos(angleRad) * 3.0;
        var dy = Math.Sin(angleRad) * 3.0;

        // 8-dir path: snapped to the nearest octant.
        Assert.Equal(Direction8.SE, CursorHeading.FromWorldVector(dx, dy, lastHeading: null, DeadZone, Hysteresis));

        // Free-angle path: the raw 26° unit vector, NOT SE's 45° (cos/sin 26° != cos/sin 45°).
        var heading = CursorHeading.FreeAngleFromWorldVector(dx, dy, DeadZone);
        Assert.NotNull(heading);
        Assert.Equal(Math.Cos(angleRad), heading!.Value.X, 6);
        Assert.Equal(Math.Sin(angleRad), heading.Value.Y, 6);
        Assert.Equal(1.0, heading.Value.Length, 9);
    }

    // ---- FREE-ANGLE A/B TEST: NearestDirection8 (the 8-way facing derivation for a raw heading) ------------------

    [Theory]
    // The raw heading still resolves to an 8-way sprite facing via the same angular sector snap FromWorldVector uses.
    [InlineData(1.0, 0.0, Direction8.E)]
    [InlineData(1.0, 1.0, Direction8.SE)]
    [InlineData(0.0, 1.0, Direction8.S)]
    [InlineData(-1.0, 1.0, Direction8.SW)]
    [InlineData(-1.0, 0.0, Direction8.W)]
    [InlineData(-1.0, -1.0, Direction8.NW)]
    [InlineData(0.0, -1.0, Direction8.N)]
    [InlineData(1.0, -1.0, Direction8.NE)]
    public void NearestDirection8_SnapsHeadingToOctant(double dx, double dy, Direction8 expected)
    {
        Assert.Equal(expected, CursorHeading.NearestDirection8(dx, dy));
    }

    [Fact]
    public void NearestDirection8_OffOctant26Deg_SnapsToSE_MatchesFromWorldVector()
    {
        // The 26° free-angle heading's 8-way facing is SE — the same octant FromWorldVector would send in 8-dir mode,
        // so the sprite faces consistently across the toggle even though the free-angle heading itself is off-octant.
        const double angleRad = 26.0 * Math.PI / 180.0;
        Assert.Equal(Direction8.SE, CursorHeading.NearestDirection8(Math.Cos(angleRad), Math.Sin(angleRad)));
    }
}
