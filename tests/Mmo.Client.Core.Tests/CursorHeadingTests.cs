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
}
