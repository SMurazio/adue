using Mmo.Shared.Domain;
using Xunit;

namespace Mmo.Shared.Tests;

// Phase 0 (continuous migration): the WorldVector position type — vector algebra + the tile/continuous bridges.
// The load-bearing case is the tile-centre round-trip identity (FromTile(t).ToTileRounded() == t), which is what
// makes the Phase-0 retype behaviour-frozen: positions only ever hold exact tile centres, and the grid/wire read
// them back losslessly via ToTileRounded.
public class WorldVectorTests
{
    private const double Eps = 1e-9;

    [Fact]
    public void ZeroIsOrigin()
    {
        Assert.Equal(0d, WorldVector.Zero.X, Eps);
        Assert.Equal(0d, WorldVector.Zero.Y, Eps);
    }

    [Fact]
    public void AddAndOperatorPlusAgree()
    {
        var a = new WorldVector(1.5, -2.0);
        var b = new WorldVector(0.5, 3.0);

        var viaMethod = a.Add(b);
        var viaOperator = a + b;

        Assert.Equal(2.0, viaMethod.X, Eps);
        Assert.Equal(1.0, viaMethod.Y, Eps);
        Assert.Equal(viaMethod, viaOperator);
    }

    [Fact]
    public void SubtractAndOperatorMinusAgree()
    {
        var a = new WorldVector(4.0, 1.0);
        var b = new WorldVector(1.5, 2.5);

        var viaMethod = a.Subtract(b);
        var viaOperator = a - b;

        Assert.Equal(2.5, viaMethod.X, Eps);
        Assert.Equal(-1.5, viaMethod.Y, Eps);
        Assert.Equal(viaMethod, viaOperator);
    }

    [Fact]
    public void ScaleScalesBothComponents()
    {
        var v = new WorldVector(2.0, -3.0);

        var scaled = v.Scale(2.5);

        Assert.Equal(5.0, scaled.X, Eps);
        Assert.Equal(-7.5, scaled.Y, Eps);
        // Both operator orders (v * s and s * v) match the method.
        Assert.Equal(scaled, v * 2.5);
        Assert.Equal(scaled, 2.5 * v);
    }

    [Fact]
    public void LengthAndLengthSquared()
    {
        var v = new WorldVector(3.0, 4.0);

        Assert.Equal(25.0, v.LengthSquared, Eps);
        Assert.Equal(5.0, v.Length, Eps);
    }

    [Fact]
    public void DotProduct()
    {
        var a = new WorldVector(1.0, 2.0);
        var b = new WorldVector(3.0, 4.0);

        Assert.Equal(11.0, a.Dot(b), Eps);
        // Perpendicular vectors dot to zero.
        Assert.Equal(0d, new WorldVector(1.0, 0.0).Dot(new WorldVector(0.0, 1.0)), Eps);
    }

    [Fact]
    public void NormalizedHasUnitLengthAndSameDirection()
    {
        var v = new WorldVector(3.0, 4.0);

        var unit = v.Normalized();

        Assert.Equal(1.0, unit.Length, Eps);
        Assert.Equal(0.6, unit.X, Eps);
        Assert.Equal(0.8, unit.Y, Eps);
    }

    [Fact]
    public void NormalizedZeroIsZeroNotNaN()
    {
        var unit = WorldVector.Zero.Normalized();

        Assert.Equal(WorldVector.Zero, unit);
        Assert.False(double.IsNaN(unit.X));
        Assert.False(double.IsNaN(unit.Y));
    }

    [Fact]
    public void FromTileGivesTileCentreCoordinates()
    {
        var fromCoord = WorldVector.FromTile(new TileCoord(7, -3));
        var fromInts = WorldVector.FromTile(7, -3);

        Assert.Equal(7.0, fromCoord.X, Eps);
        Assert.Equal(-3.0, fromCoord.Y, Eps);
        Assert.Equal(fromCoord, fromInts);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(5, 9)]
    [InlineData(-4, 12)]
    [InlineData(123, -456)]
    public void TileCentreRoundTripIsIdentity(int x, int y)
    {
        // The Phase-0 invariant: a position built from a tile centre rounds back to that exact tile.
        var tile = new TileCoord(x, y);

        var roundTripped = WorldVector.FromTile(tile).ToTileRounded();

        Assert.Equal(tile, roundTripped);
    }

    [Fact]
    public void ToTileRoundedRoundsToNearestCentre()
    {
        Assert.Equal(new TileCoord(2, 3), new WorldVector(2.4, 2.6).ToTileRounded());
        Assert.Equal(new TileCoord(-1, 1), new WorldVector(-0.6, 0.9).ToTileRounded());
        // Round-away-from-zero on the .5 boundary (deterministic).
        Assert.Equal(new TileCoord(3, -3), new WorldVector(2.5, -2.5).ToTileRounded());
    }

    [Fact]
    public void ToTileFlooredFloorsEachAxis()
    {
        Assert.Equal(new TileCoord(2, 2), new WorldVector(2.9, 2.1).ToTileFloored());
        Assert.Equal(new TileCoord(-1, -2), new WorldVector(-0.1, -1.5).ToTileFloored());
        // At an exact tile centre, floor equals the tile.
        Assert.Equal(new TileCoord(4, 4), new WorldVector(4.0, 4.0).ToTileFloored());
    }
}
