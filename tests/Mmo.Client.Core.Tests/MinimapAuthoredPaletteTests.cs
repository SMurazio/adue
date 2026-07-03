using Mmo.Client.Core;
using Mmo.Shared.Domain;
using Xunit;

namespace Mmo.Client.Core.Tests;

// N (todo/N-minimap-384-bake-cost.md item 2): the minimap's authored base-layer palette + raw-byte bake
// loop, headlessly tested. Mirrors AuthoredSurfaceVisualsTests' fixture style — one tile of every char
// class — so the minimap's classification can be checked against the SAME ground truth the 3D floor uses.
public sealed class MinimapAuthoredPaletteTests
{
    private static readonly (byte R, byte G, byte B, byte A) WallColor = (200, 210, 220, 255);
    private const byte FloorAlpha = 234; // ~0.92, matches the minimap's existing translucent overlay look.

    // '#' wall, '.' grass, '~' water, ' ' out-of-world / ',' dirt, ':' cobble, '-' stone, 'S' spawn (cobble).
    private static AuthoredMap Map()
    {
        return AuthoredMap.Parse(
        [
            "#.~ ",
            ",:-S",
        ]);
    }

    [Fact]
    public void OutOfWorldTileIsFullyTransparent()
    {
        var rgba = MinimapAuthoredPalette.TileRgba(Map(), new TileCoord(3, 0), WallColor, FloorAlpha);
        Assert.Equal(0, rgba.R);
        Assert.Equal(0, rgba.G);
        Assert.Equal(0, rgba.B);
        Assert.Equal(0, rgba.A);
    }

    [Fact]
    public void WallTileUsesTheGivenWallColorNotTheFloorPalette()
    {
        var rgba = MinimapAuthoredPalette.TileRgba(Map(), new TileCoord(0, 0), WallColor, FloorAlpha);
        Assert.Equal(WallColor, rgba);
    }

    [Fact]
    public void WaterTileUsesItsOwnAlbedoNotTheWallColor()
    {
        // Water is blocked but must NOT read as a wall — a gray box on a pond reads wrong (M2 water
        // decision), and the same is true on the minimap: it must show the blue anchor, not gray.
        var rgba = MinimapAuthoredPalette.TileRgba(Map(), new TileCoord(2, 0), WallColor, FloorAlpha);
        Assert.NotEqual(WallColor, rgba);
        Assert.Equal(FloorAlpha, rgba.A);

        var expected = AuthoredSurfaceVisuals.Albedo(SurfaceCategory.Water);
        Assert.Equal((byte)Math.Round(expected.R * 255f), rgba.R);
        Assert.Equal((byte)Math.Round(expected.G * 255f), rgba.G);
        Assert.Equal((byte)Math.Round(expected.B * 255f), rgba.B);
    }

    [Theory]
    [InlineData(1, 0, SurfaceCategory.Grass)]   // '.'
    [InlineData(0, 1, SurfaceCategory.Dirt)]    // ','
    [InlineData(1, 1, SurfaceCategory.Cobble)]  // ':'
    [InlineData(2, 1, SurfaceCategory.DungeonStone)] // '-'
    [InlineData(3, 1, SurfaceCategory.Cobble)]  // 'S' spawn anchor -> cobble
    public void WalkableTileMatchesAuthoredSurfaceVisualsAlbedo(int x, int y, SurfaceCategory category)
    {
        var rgba = MinimapAuthoredPalette.TileRgba(Map(), new TileCoord(x, y), WallColor, FloorAlpha);
        var albedo = AuthoredSurfaceVisuals.Albedo(category);

        Assert.Equal((byte)Math.Round(albedo.R * 255f), rgba.R);
        Assert.Equal((byte)Math.Round(albedo.G * 255f), rgba.G);
        Assert.Equal((byte)Math.Round(albedo.B * 255f), rgba.B);
        Assert.Equal(FloorAlpha, rgba.A);
    }

    [Fact]
    public void BakeBaseLayerProducesTheRightSizedBuffer()
    {
        var map = Map();
        const int scale = 3;
        var bytes = MinimapAuthoredPalette.BakeBaseLayer(map, scale, WallColor, FloorAlpha);

        Assert.Equal(map.Width * scale * map.Height * scale * 4, bytes.Length);
    }

    [Fact]
    public void BakeBaseLayerStampsEachTileAsASolidScaleXScaleBlockWithNoBleed()
    {
        var map = Map();
        const int scale = 2;
        var bytes = MinimapAuthoredPalette.BakeBaseLayer(map, scale, WallColor, FloorAlpha);
        var pxWidth = map.Width * scale;

        // Tile (0,0) is the wall '#' -> every pixel in its 2x2 block must be the wall color.
        for (var dy = 0; dy < scale; dy++)
        {
            for (var dx = 0; dx < scale; dx++)
            {
                Assert.Equal(WallColor, PixelAt(bytes, pxWidth, dx, dy));
            }
        }

        // Tile (1,0) is grass '.' -> its block must be the grass albedo, distinct from the wall block,
        // proving the stamp did not bleed across the tile boundary.
        var grass = AuthoredSurfaceVisuals.Albedo(SurfaceCategory.Grass);
        var expectedGrass = (
            (byte)Math.Round(grass.R * 255f), (byte)Math.Round(grass.G * 255f), (byte)Math.Round(grass.B * 255f), FloorAlpha);
        for (var dy = 0; dy < scale; dy++)
        {
            for (var dx = 0; dx < scale; dx++)
            {
                Assert.Equal(expectedGrass, PixelAt(bytes, pxWidth, scale + dx, dy));
            }
        }
    }

    [Fact]
    public void BakeBaseLayerLeavesOutOfWorldPixelsTransparent()
    {
        var map = Map();
        const int scale = 2;
        var bytes = MinimapAuthoredPalette.BakeBaseLayer(map, scale, WallColor, FloorAlpha);
        var pxWidth = map.Width * scale;

        // Tile (3,0) is out-of-world padding (' ') -> its whole block stays zero (transparent).
        for (var dy = 0; dy < scale; dy++)
        {
            for (var dx = 0; dx < scale; dx++)
            {
                var pixel = PixelAt(bytes, pxWidth, (3 * scale) + dx, dy);
                Assert.Equal(0, pixel.Item1);
                Assert.Equal(0, pixel.Item2);
                Assert.Equal(0, pixel.Item3);
                Assert.Equal(0, pixel.Item4);
            }
        }
    }

    [Fact]
    public void BakeBaseLayerRejectsNonPositiveScale()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MinimapAuthoredPalette.BakeBaseLayer(Map(), 0, WallColor, FloorAlpha));
    }

    private static (byte, byte, byte, byte) PixelAt(byte[] bytes, int pxWidth, int x, int y)
    {
        var i = ((y * pxWidth) + x) * 4;
        return (bytes[i], bytes[i + 1], bytes[i + 2], bytes[i + 3]);
    }
}
