using Mmo.Client.Core;
using Mmo.Shared.Domain;
using Xunit;

namespace Mmo.Client.Core.Tests;

// S56 mouse hold-to-walk-toward-cursor: the world-space tile delta from the player to the cursor tile maps to
// the NEAREST of the 8 movement directions (45° sectors). +X = east, +Y = south (screen-down). These tests
// pin every sector centre, the same-tile (no heading) case, and the "mostly one axis" rounding that
// distinguishes this from a sign-per-axis heading.
public sealed class CursorHeadingTests
{
    private static readonly TileCoord Player = new(10, 10);

    [Theory]
    // Cardinal/diagonal sector centres (the 8 unit deltas), world axes +X=east, +Y=south.
    [InlineData(0, -1, Direction8.N)]
    [InlineData(1, -1, Direction8.NE)]
    [InlineData(1, 0, Direction8.E)]
    [InlineData(1, 1, Direction8.SE)]
    [InlineData(0, 1, Direction8.S)]
    [InlineData(-1, 1, Direction8.SW)]
    [InlineData(-1, 0, Direction8.W)]
    [InlineData(-1, -1, Direction8.NW)]
    public void MapsUnitDeltaToCardinalAndDiagonalDirections(int dx, int dy, Direction8 expected)
    {
        var to = new TileCoord(Player.X + dx, Player.Y + dy);
        Assert.Equal(expected, CursorHeading.FromTileDelta(Player, to));
    }

    [Fact]
    public void SameTile_ReturnsNull_NoHeading()
    {
        Assert.Null(CursorHeading.FromTileDelta(Player, Player));
    }

    [Theory]
    // A far, mostly-east cursor (10 east, 1 south) is closest to E, not SE — nearest-sector, not sign-per-axis.
    [InlineData(10, 1, Direction8.E)]
    [InlineData(10, -1, Direction8.E)]
    // Mostly-south (1 east, 10 south) snaps to S.
    [InlineData(1, 10, Direction8.S)]
    [InlineData(-1, 10, Direction8.S)]
    // A true 2:1 lean still rounds to the nearest sector: 5 east / 2 south (~21.8°) -> E (closer to 0° than 45°).
    [InlineData(5, 2, Direction8.E)]
    // 2 east / 5 south (~68°) -> S (closer to 90° than 45°).
    [InlineData(2, 5, Direction8.S)]
    // Near-perfect diagonal (5 east / 4 south, ~38.7°) rounds to SE.
    [InlineData(5, 4, Direction8.SE)]
    public void MapsFarOffAxisDeltaToNearestSector(int dx, int dy, Direction8 expected)
    {
        var to = new TileCoord(Player.X + dx, Player.Y + dy);
        Assert.Equal(expected, CursorHeading.FromTileDelta(Player, to));
    }
}
