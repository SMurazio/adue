using Mmo.Shared.Domain;

namespace Mmo.Client.Core;

// N (todo/N-minimap-384-bake-cost.md item 2): the minimap's AUTHORED (genVersion 2) base-layer palette
// + raw-byte baker. Fixes the live-user-facing bug where the minimap kept drawing the legacy terrain.png
// bitmap's fictional trails on an authored zone while the real road sat 60 tiles away — the base layer
// must read the SAME ground truth the player walks on. Godot-free so both the per-tile color mapping and
// the bake loop are headlessly testable; Minimap.cs (Godot) is a thin "call this, wrap the bytes in an
// Image" shell. Reuses AuthoredSurfaceVisuals.Albedo — the SAME color table the 3D floor is painted with
// — instead of a second, driftable copy of the palette.
public static class MinimapAuthoredPalette
{
    // The RGBA (0-255) color for one tile of the baked base layer:
    //   * out-of-world (nothing exists there, matches the 3D painter drawing no floor) -> fully transparent.
    //   * blocked + Water -> the Water albedo (blue visual anchor), NOT the wall color — a gray box painted
    //     over a pond reads wrong on the minimap for the same reason it reads wrong in 3D (M2 water decision).
    //   * blocked + anything else (a real wall) -> `wallColor` (the minimap's own contrasting wall tint;
    //     unrelated to the 3D floor palette, so it stays whatever Minimap.cs already uses).
    //   * walkable -> that tile's SurfaceCategory albedo.
    // `floorAlpha` is the opacity applied to every painted (non-transparent, non-wall) floor tile, so the
    // caller can keep the minimap's existing translucent-overlay look; wallColor carries its own alpha.
    public static (byte R, byte G, byte B, byte A) TileRgba(
        AuthoredMap authored, TileCoord tile, (byte R, byte G, byte B, byte A) wallColor, byte floorAlpha)
    {
        ArgumentNullException.ThrowIfNull(authored);

        if (authored.IsOutOfWorld(tile))
        {
            return (0, 0, 0, 0);
        }

        if (authored.IsBlocked(tile))
        {
            return authored.CategoryAt(tile) == SurfaceCategory.Water
                ? Quantize(AuthoredSurfaceVisuals.Albedo(SurfaceCategory.Water), floorAlpha)
                : wallColor;
        }

        return Quantize(AuthoredSurfaceVisuals.Albedo(authored.CategoryAt(tile)), floorAlpha);
    }

    private static (byte, byte, byte, byte) Quantize((float R, float G, float B) albedo, byte alpha)
    {
        return (QuantizeChannel(albedo.R), QuantizeChannel(albedo.G), QuantizeChannel(albedo.B), alpha);
    }

    private static byte QuantizeChannel(float value)
    {
        return (byte)Math.Clamp(MathF.Round(value * 255f), 0f, 255f);
    }

    // Bakes the FULL authored base layer into one RGBA8 byte buffer, row-major pixels (y outer, x inner;
    // 4 bytes/pixel), each tile stamped as a `scale` x `scale` flat-color block. The caller builds ONE
    // Image via Image.CreateFromData from this instead of Width*Height*scale^2 individual SetPixel interop
    // calls (~5M calls at 384x384 pre-fix) — direct array writes, and this runs ONCE per zone (bake at a
    // fixed `scale`, independent of the live minimap zoom), not once per zoom click.
    public static byte[] BakeBaseLayer(
        AuthoredMap authored, int scale, (byte R, byte G, byte B, byte A) wallColor, byte floorAlpha)
    {
        ArgumentNullException.ThrowIfNull(authored);
        if (scale < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(scale), scale, "Bake scale must be >= 1.");
        }

        var pxWidth = authored.Width * scale;
        var pxHeight = authored.Height * scale;
        var bytes = new byte[pxWidth * pxHeight * 4];

        for (var ty = 0; ty < authored.Height; ty++)
        {
            for (var tx = 0; tx < authored.Width; tx++)
            {
                var tile = new TileCoord(tx, ty);
                if (authored.IsOutOfWorld(tile))
                {
                    continue; // buffer is already zero-initialized (fully transparent) — nothing to stamp.
                }

                var (r, g, b, a) = TileRgba(authored, tile, wallColor, floorAlpha);
                MinimapRasterBytes.StampBlock(bytes, pxWidth, tx * scale, ty * scale, scale, r, g, b, a);
            }
        }

        return bytes;
    }
}
