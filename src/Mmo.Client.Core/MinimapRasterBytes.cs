namespace Mmo.Client.Core;

// N (todo/N-minimap-384-bake-cost.md item 1): generic RGBA8 raw-byte raster primitives shared by BOTH
// minimap bake paths (the authored SurfaceCategory bake in MinimapAuthoredPalette, and the legacy
// terrain.png bake in Minimap.cs) — one tested "write a solid tile-block / fill / border-line into a byte
// buffer" core instead of two copies of the same loop. Godot-free: the caller builds the Image from the
// finished buffer via ONE Image.CreateFromData call instead of per-pixel SetPixel/GetPixel interop.
public static class MinimapRasterBytes
{
    // Fills every pixel of a `pxWidth` x `pxHeight` RGBA8 buffer with one color.
    public static void FillAll(byte[] bytes, byte r, byte g, byte b, byte a)
    {
        for (var i = 0; i < bytes.Length; i += 4)
        {
            bytes[i] = r;
            bytes[i + 1] = g;
            bytes[i + 2] = b;
            bytes[i + 3] = a;
        }
    }

    // Writes a `size` x `size` solid-color block with its top-left corner at pixel (px0, py0) — the raster
    // form of "paint one map tile at the current bake scale". `pxWidth` is the full buffer's row stride in
    // pixels (needed to compute row offsets); the caller is responsible for keeping px0/py0/size in bounds.
    public static void StampBlock(byte[] bytes, int pxWidth, int px0, int py0, int size, byte r, byte g, byte b, byte a)
    {
        for (var dy = 0; dy < size; dy++)
        {
            var rowStart = (((py0 + dy) * pxWidth) + px0) * 4;
            for (var dx = 0; dx < size; dx++)
            {
                var i = rowStart + (dx * 4);
                bytes[i] = r;
                bytes[i + 1] = g;
                bytes[i + 2] = b;
                bytes[i + 3] = a;
            }
        }
    }

    // Writes a 1px border along the four edges of a `pxWidth` x `pxHeight` buffer (the minimap's "world
    // bounds" decoration) — same visual as the pre-N per-pixel SetPixel version, now direct array writes.
    public static void StampBorder(byte[] bytes, int pxWidth, int pxHeight, byte r, byte g, byte b, byte a)
    {
        for (var x = 0; x < pxWidth; x++)
        {
            SetPixel(bytes, pxWidth, x, 0, r, g, b, a);
            SetPixel(bytes, pxWidth, x, pxHeight - 1, r, g, b, a);
        }

        for (var y = 0; y < pxHeight; y++)
        {
            SetPixel(bytes, pxWidth, 0, y, r, g, b, a);
            SetPixel(bytes, pxWidth, pxWidth - 1, y, r, g, b, a);
        }
    }

    private static void SetPixel(byte[] bytes, int pxWidth, int x, int y, byte r, byte g, byte b, byte a)
    {
        var i = ((y * pxWidth) + x) * 4;
        bytes[i] = r;
        bytes[i + 1] = g;
        bytes[i + 2] = b;
        bytes[i + 3] = a;
    }
}
