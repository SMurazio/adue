using Mmo.Shared.Domain;
using Xunit;

namespace Mmo.Shared.Tests;

// COMBAT-S2B: the melee-cone tile math. The cone is the forward tile plus its two 45° flank tiles, one tile out,
// rotated by the attacker's Direction8 facing. These pin the absolute tiles per facing (cardinal + diagonal),
// that the fan rotates with facing, and that the three tiles are always distinct.
public sealed class MeleeConeTests
{
    private static TileCoord[] Resolve(TileCoord origin, Direction8 facing)
    {
        Span<TileCoord> buffer = stackalloc TileCoord[MeleeCone.TileCount];
        var count = MeleeCone.Resolve(origin, facing, buffer);
        Assert.Equal(MeleeCone.TileCount, count);
        return buffer.ToArray();
    }

    [Fact]
    public void NorthFacingConeIsForwardAndTwoFlanks()
    {
        // Facing N (0,-1): forward = N tile; flanks are NW (-1,-1) and NE (1,-1), one tile out from the origin.
        var origin = new TileCoord(10, 10);

        var cone = Resolve(origin, Direction8.N);

        Assert.Contains(new TileCoord(10, 9), cone);  // forward (N)
        Assert.Contains(new TileCoord(9, 9), cone);   // left flank (NW)
        Assert.Contains(new TileCoord(11, 9), cone);  // right flank (NE)
    }

    [Fact]
    public void EastFacingConeIsForwardAndTwoFlanks()
    {
        // Facing E (1,0): forward = E tile; flanks are NE (1,-1) and SE (1,1).
        var origin = new TileCoord(5, 5);

        var cone = Resolve(origin, Direction8.E);

        Assert.Contains(new TileCoord(6, 5), cone);   // forward (E)
        Assert.Contains(new TileCoord(6, 4), cone);   // left flank (NE)
        Assert.Contains(new TileCoord(6, 6), cone);   // right flank (SE)
    }

    [Fact]
    public void SouthFacingConeIsForwardAndTwoFlanks()
    {
        var origin = new TileCoord(0, 0);

        var cone = Resolve(origin, Direction8.S);

        Assert.Contains(new TileCoord(0, 1), cone);   // forward (S)
        Assert.Contains(new TileCoord(1, 1), cone);   // left flank (SE)
        Assert.Contains(new TileCoord(-1, 1), cone);  // right flank (SW)
    }

    [Fact]
    public void DiagonalFacingConeRotatesWithFacing()
    {
        // Facing NE (1,-1): forward = NE; the flanks are the adjacent compass directions N (0,-1) and E (1,0),
        // one tile out. This is the design's open "diagonal rotation" case — it falls out as the adjacent
        // Direction8 values with no special-casing.
        var origin = new TileCoord(20, 20);

        var cone = Resolve(origin, Direction8.NE);

        Assert.Contains(new TileCoord(21, 19), cone); // forward (NE)
        Assert.Contains(new TileCoord(20, 19), cone); // left flank (N)
        Assert.Contains(new TileCoord(21, 20), cone); // right flank (E)
    }

    [Theory]
    [InlineData(Direction8.N)]
    [InlineData(Direction8.NE)]
    [InlineData(Direction8.E)]
    [InlineData(Direction8.SE)]
    [InlineData(Direction8.S)]
    [InlineData(Direction8.SW)]
    [InlineData(Direction8.W)]
    [InlineData(Direction8.NW)]
    public void ConeTilesAreDistinctAndOneTileOut(Direction8 facing)
    {
        var origin = new TileCoord(8, 8);

        var cone = Resolve(origin, facing);

        // Three distinct tiles (no aliasing).
        Assert.Equal(3, cone.Length);
        Assert.Equal(3, new HashSet<TileCoord>(cone).Count);

        // Every cone tile is exactly one tile (Chebyshev) from the origin, and none is the origin itself.
        foreach (var tile in cone)
        {
            Assert.NotEqual(origin, tile);
            var dx = Math.Abs(tile.X - origin.X);
            var dy = Math.Abs(tile.Y - origin.Y);
            Assert.Equal(1, Math.Max(dx, dy));
        }
    }
}
