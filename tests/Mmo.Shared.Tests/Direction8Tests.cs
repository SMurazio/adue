using Mmo.Shared.Domain;
using Xunit;

namespace Mmo.Shared.Tests;

// Phase 1 (continuous migration): Direction8.ToUnitVector() — the bridge from the discrete 8-way input direction to
// the unit WorldVector the continuous integrator scales by speed. The load-bearing property is that EVERY direction
// (cardinal and diagonal) is length 1, so a held diagonal is NOT faster than a held cardinal once scaled by speed.
public class Direction8Tests
{
    private const double Eps = 1e-9;

    [Theory]
    [InlineData(Direction8.N, 0d, -1d)]
    [InlineData(Direction8.E, 1d, 0d)]
    [InlineData(Direction8.S, 0d, 1d)]
    [InlineData(Direction8.W, -1d, 0d)]
    public void CardinalUnitVectorsMatchTheTileDelta(Direction8 direction, double x, double y)
    {
        var unit = direction.ToUnitVector();
        Assert.Equal(x, unit.X, Eps);
        Assert.Equal(y, unit.Y, Eps);
        Assert.Equal(1d, unit.Length, Eps);
    }

    [Theory]
    [InlineData(Direction8.NE)]
    [InlineData(Direction8.SE)]
    [InlineData(Direction8.SW)]
    [InlineData(Direction8.NW)]
    public void DiagonalUnitVectorsAreNormalizedToLengthOne(Direction8 direction)
    {
        var unit = direction.ToUnitVector();
        // A diagonal tile delta is (±1, ±1), length sqrt(2); normalization must bring it to length 1 (each axis
        // ±1/sqrt(2)) so a diagonal is not ~41% faster than a cardinal.
        Assert.Equal(1d, unit.Length, Eps);
        Assert.Equal(1d / System.Math.Sqrt(2d), System.Math.Abs(unit.X), Eps);
        Assert.Equal(1d / System.Math.Sqrt(2d), System.Math.Abs(unit.Y), Eps);
    }

    [Fact]
    public void EveryDirectionIsUnitLength()
    {
        foreach (Direction8 direction in System.Enum.GetValues<Direction8>())
        {
            Assert.Equal(1d, direction.ToUnitVector().Length, Eps);
        }
    }

    [Fact]
    public void UnitVectorSignsMatchTheTileDelta()
    {
        // The unit vector must point the SAME way as the integer tile delta (just rescaled), so the integrator
        // and the tile-stepped monster path agree on which way each direction faces.
        foreach (Direction8 direction in System.Enum.GetValues<Direction8>())
        {
            var delta = direction.Delta();
            var unit = direction.ToUnitVector();
            Assert.Equal(System.Math.Sign(delta.X), System.Math.Sign(unit.X));
            Assert.Equal(System.Math.Sign(delta.Y), System.Math.Sign(unit.Y));
        }
    }
}
