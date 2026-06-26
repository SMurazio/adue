namespace Mmo.Shared.Domain;

// FREEAIM: the continuous aim angle quantization shared by client (encode) and server (decode). The aim is a
// world-plane BEARING in radians; on the wire it is a single ushort mapping the full 0..65535 range onto the
// circle [0, 2π). Resolution is 2π/65536 ≈ 0.0096 mrad ≈ 0.0055° — far finer than any feel difference, in 2
// bytes. The mapping wraps at the seam (65535 ≈ 359.99°, then back to 0), so there is no discontinuity the
// resolver must special-case: a bearing difference is always taken modulo 2π.
//
// Bearing convention (matches the client's world plane): angle is atan2(dz, dx) where +X is east (tile +X) and
// +Z is south (tile +Y). 0 = east, +π/2 = south, etc. The server's sector test reduces (entityBearing - aim)
// to (-π, π] and hits when |delta| <= halfAngle, so the convention only has to be CONSISTENT across encode and
// the resolver — which it is, because both go through this one type.
public static class AimAngle
{
    private const double TwoPi = 2d * System.Math.PI;

    // Quantize a radians angle to the wire ushort. Normalizes into [0, 2π) first so any input (negative, > 2π)
    // maps cleanly, then scales onto 0..65536 and wraps so 2π folds back to 0.
    public static ushort Quantize(double radians)
    {
        var normalized = radians % TwoPi;
        if (normalized < 0)
        {
            normalized += TwoPi;
        }

        var scaled = (long)System.Math.Round(normalized / TwoPi * 65536d);
        // Round can land on 65536 (a hair under 2π rounding up) — wrap it to 0 so the value fits a ushort and the
        // seam is continuous.
        return (ushort)(scaled & 0xFFFF);
    }

    // Decode a wire ushort back to radians in [0, 2π). Exact inverse of Quantize up to the quantization step.
    public static double ToRadians(ushort quantized)
    {
        return quantized / 65536d * TwoPi;
    }

    // MOVEMENT-ACTIONS Phase B1: decode a wire bearing ushort straight to a UNIT world-plane direction vector
    // (cos θ, sin θ) under the SAME convention as the aim/sector path: θ = atan2(dz, dx), +X east, +Z south (tile +Y),
    // so x = cos θ, y = sin θ. The action stream sends a launch HEADING as exactly this bearing (reusing AimAngle's
    // quantization), and the server resolves it to a unit heading the ServerActionExecutor takes — no second
    // quantization convention. Always unit-length (a degenerate angle can't, since cos²+sin² == 1).
    public static WorldVector ToUnitVector(ushort quantized)
    {
        var radians = ToRadians(quantized);
        return new WorldVector(System.Math.Cos(radians), System.Math.Sin(radians));
    }
}
