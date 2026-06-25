using Mmo.Shared.Domain;

namespace Mmo.Shared.Protocol;

// Fixed-point Q12.4 codec for the HOT per-entity snapshot position (continuous-migration Phase 3 wire).
// A WorldVector axis (double, 1.0 == one tile) is quantized to a signed 16-bit integer of SIXTEENTHS of a
// tile: qx = round(X * 16). That is 1/16-tile (0.0625 u) precision and keeps the position at 4 bytes/entity
// (two shorts) — zero inflation vs the tile-only wire today, where float32 would be +33% on the hot record.
//
// WHY round-AWAY-FROM-ZERO: matches WorldVector.ToTileRounded so the quantizer and the tile-rounding path
// agree on the .5 boundary (deterministic, symmetric across the origin). An exact tile centre (integer axis)
// encodes losslessly: round(n * 16) == n * 16, and Decode gives n * 16 / 16 == n back exactly.
//
// QUANTIZE ON SEND ONLY: the server's authoritative Position stays full-precision double; this is a lossy
// projection applied at serialize time, never fed back into the sim (which would break determinism vs the
// client predictor). NOTHING is wired to this in Pass A — it is additive infrastructure for the Pass B wire.
public static class PositionEncoding
{
    // Q12.4: 4 fractional bits → scale 16. Decode divides by the same scale.
    public const int FixedPointShift = 4;

    public const double Scale = 1 << FixedPointShift; // 16.0

    // The representable axis range in TILES: a signed 16-bit count of sixteenths spans
    // [short.MinValue, short.MaxValue] / 16 ≈ ±2048 tiles. Encoding past this would silently wrap the short,
    // so Encode guards it and throws instead.
    public const double MaxAbsTile = short.MaxValue / Scale; // 2047.9375

    public static (short Qx, short Qy) Encode(WorldVector position)
    {
        return (EncodeAxis(position.X), EncodeAxis(position.Y));
    }

    public static WorldVector Decode(short qx, short qy)
    {
        return new WorldVector(qx / Scale, qy / Scale);
    }

    private static short EncodeAxis(double axis)
    {
        if (axis < short.MinValue / Scale || axis > MaxAbsTile)
        {
            throw new ProtocolException(
                $"Position axis {axis} is out of fixed-point range (±{MaxAbsTile} tiles).");
        }

        // Round-away-from-zero matches WorldVector.ToTileRounded so the two rounding paths never disagree.
        var quantized = System.Math.Round(axis * Scale, System.MidpointRounding.AwayFromZero);
        return (short)quantized;
    }
}
