using Mmo.Client.Core;
using Xunit;

namespace Mmo.Client.Core.Tests;

// N (todo/N-minimap-384-bake-cost.md item 1): the generic RGBA8 raster primitives both minimap bake paths
// share (authored SurfaceCategory bake + legacy terrain.png bake) — the batched-write replacement for
// per-pixel SetPixel/GetPixel interop calls. Pure array math, no Godot.
public sealed class MinimapRasterBytesTests
{
    [Fact]
    public void FillAllSetsEveryPixel()
    {
        var bytes = new byte[4 * 3 * 4]; // 4x3 px
        MinimapRasterBytes.FillAll(bytes, 10, 20, 30, 40);

        for (var i = 0; i < bytes.Length; i += 4)
        {
            Assert.Equal(10, bytes[i]);
            Assert.Equal(20, bytes[i + 1]);
            Assert.Equal(30, bytes[i + 2]);
            Assert.Equal(40, bytes[i + 3]);
        }
    }

    [Fact]
    public void StampBlockPaintsOnlyItsOwnRegionWithNoBleed()
    {
        const int pxWidth = 6;
        const int pxHeight = 4;
        var bytes = new byte[pxWidth * pxHeight * 4];

        // A 2x2 block at (2,1) must not touch anything outside [2,4) x [1,3).
        MinimapRasterBytes.StampBlock(bytes, pxWidth, 2, 1, 2, 100, 110, 120, 130);

        for (var y = 0; y < pxHeight; y++)
        {
            for (var x = 0; x < pxWidth; x++)
            {
                var i = ((y * pxWidth) + x) * 4;
                var inside = x is 2 or 3 && y is 1 or 2;
                if (inside)
                {
                    Assert.Equal(100, bytes[i]);
                    Assert.Equal(110, bytes[i + 1]);
                    Assert.Equal(120, bytes[i + 2]);
                    Assert.Equal(130, bytes[i + 3]);
                }
                else
                {
                    Assert.Equal(0, bytes[i]);
                    Assert.Equal(0, bytes[i + 1]);
                    Assert.Equal(0, bytes[i + 2]);
                    Assert.Equal(0, bytes[i + 3]);
                }
            }
        }
    }

    [Fact]
    public void StampBorderPaintsOnlyTheOuterRing()
    {
        const int pxWidth = 5;
        const int pxHeight = 4;
        var bytes = new byte[pxWidth * pxHeight * 4];

        MinimapRasterBytes.StampBorder(bytes, pxWidth, pxHeight, 200, 201, 202, 203);

        for (var y = 0; y < pxHeight; y++)
        {
            for (var x = 0; x < pxWidth; x++)
            {
                var i = ((y * pxWidth) + x) * 4;
                var onEdge = x == 0 || y == 0 || x == pxWidth - 1 || y == pxHeight - 1;
                if (onEdge)
                {
                    Assert.Equal(200, bytes[i]);
                    Assert.Equal(201, bytes[i + 1]);
                    Assert.Equal(202, bytes[i + 2]);
                    Assert.Equal(203, bytes[i + 3]);
                }
                else
                {
                    Assert.Equal(0, bytes[i]);
                }
            }
        }
    }
}
