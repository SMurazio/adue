using Mmo.Shared.Domain;
using Mmo.Shared.Protocol;
using Xunit;

namespace Mmo.Shared.Tests;

// Phase 3 (continuous migration): the fixed-point Q12.4 codec for the hot per-entity snapshot position.
// Load-bearing invariants: exact tile centres round-trip losslessly (so Pass A — which still sends tile-centred
// positions — stays byte-identical), fractional positions round-trip within 1/16 u, the .5 boundary rounds
// away from zero (matching WorldVector.ToTileRounded), and out-of-range axes are rejected, not silently wrapped.
public class PositionEncodingTests
{
    private const double Step = 1.0 / 16.0; // the quantization step (0.0625 u)

    [Theory]
    [InlineData(0, 0)]
    [InlineData(5, 9)]
    [InlineData(-4, 12)]
    [InlineData(123, -456)]
    [InlineData(2047, -2047)]
    public void TileCentresRoundTripLosslessly(int x, int y)
    {
        // An integer (tile-centre) axis encodes with no loss: round(n*16)/16 == n exactly. This is what makes
        // Pass A's tile-centred wire byte-identical when it later flows through this encoder.
        var original = WorldVector.FromTile(x, y);

        var (qx, qy) = PositionEncoding.Encode(original);
        var decoded = PositionEncoding.Decode(qx, qy);

        Assert.Equal(original.X, decoded.X, 0d);
        Assert.Equal(original.Y, decoded.Y, 0d);
    }

    [Theory]
    [InlineData(0.0625, 0.0625)]   // exactly one step
    [InlineData(1.3, -2.7)]
    [InlineData(12.04, -8.99)]
    [InlineData(-0.03125, 0.09)]   // sub-step magnitudes
    [InlineData(2000.51, -1999.49)]
    public void FractionalPositionsRoundTripWithinOneSixteenth(double x, double y)
    {
        var original = new WorldVector(x, y);

        var (qx, qy) = PositionEncoding.Encode(original);
        var decoded = PositionEncoding.Decode(qx, qy);

        // Quantization error is at most half a step on each axis, never more than a full step.
        Assert.True(System.Math.Abs(decoded.X - original.X) <= Step + 1e-12);
        Assert.True(System.Math.Abs(decoded.Y - original.Y) <= Step + 1e-12);
    }

    [Fact]
    public void EncodeIsDeterministic()
    {
        // Same double in → same shorts out, every time (the encoder must be a pure function for byte-precision).
        var v = new WorldVector(3.14159, -2.71828);

        var first = PositionEncoding.Encode(v);
        var second = PositionEncoding.Encode(v);

        Assert.Equal(first, second);
    }

    [Fact]
    public void RoundsAwayFromZeroOnHalfStepBoundary()
    {
        // A value exactly on the .5-of-a-step boundary rounds away from zero on BOTH signs (matching
        // WorldVector.ToTileRounded's convention), so quantization is symmetric across the origin.
        // 1.5 sixteenths boundary: 0.09375 == 1.5/16; positive -> 2/16, negative -> -2/16.
        var (qxPos, _) = PositionEncoding.Encode(new WorldVector(0.09375, 0));
        var (qxNeg, _) = PositionEncoding.Encode(new WorldVector(-0.09375, 0));

        Assert.Equal((short)2, qxPos);
        Assert.Equal((short)-2, qxNeg);
    }

    [Fact]
    public void EncodeThrowsWhenAxisExceedsRange()
    {
        // Past ±~2048 tiles a short would silently wrap; the encoder rejects it instead.
        Assert.Throws<ProtocolException>(() => PositionEncoding.Encode(new WorldVector(3000, 0)));
        Assert.Throws<ProtocolException>(() => PositionEncoding.Encode(new WorldVector(0, -3000)));
    }

    [Fact]
    public void EncodesUpToTheRangeBound()
    {
        // The largest representable axis (short.MaxValue/16) encodes and decodes without throwing.
        var atBound = new WorldVector(PositionEncoding.MaxAbsTile, -PositionEncoding.MaxAbsTile);

        var (qx, qy) = PositionEncoding.Encode(atBound);
        var decoded = PositionEncoding.Decode(qx, qy);

        Assert.Equal(short.MaxValue, qx);
        Assert.Equal((short)(-short.MaxValue), qy);
        Assert.Equal(atBound.X, decoded.X, 0d);
        Assert.Equal(atBound.Y, decoded.Y, 0d);
    }
}
