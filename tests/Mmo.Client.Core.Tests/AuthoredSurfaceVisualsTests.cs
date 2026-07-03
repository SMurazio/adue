using Mmo.Client.Core;
using Mmo.Shared.Domain;
using Xunit;

namespace Mmo.Client.Core.Tests;

// M2 (docs/town-floor1-blockout-design.md): the pure half of the authored-map graybox floor — the
// category→albedo palette, the wall-box rule (water/out-of-world draw no box), and the per-chunk
// per-category tile grouping. All headless: the Godot painter is a thin shell over these.
public sealed class AuthoredSurfaceVisualsTests
{
    // One tile of every char class: wall, grass, water, out-of-world / dirt, cobble, stone, spawn /
    // markers (walkable grass) and plain grass. 4 wide x 3 tall.
    private static AuthoredMap Map()
    {
        return AuthoredMap.Parse(
        [
            "#.~ ",
            ",:-S",
            "H.T.",
        ]);
    }

    [Fact]
    public void CategoryCountCoversTheWholeEnum()
    {
        // The painter sizes its material/instance arrays by CategoryCount and indexes them by the raw
        // byte value — a new enum member without a palette entry must fail HERE, not as an index crash.
        var values = Enum.GetValues<SurfaceCategory>();
        Assert.Equal(AuthoredSurfaceVisuals.CategoryCount, values.Length);
        foreach (var value in values)
        {
            Assert.InRange((int)value, 0, AuthoredSurfaceVisuals.CategoryCount - 1);
        }
    }

    [Fact]
    public void AlbedoTintsMatchTheirHueContract()
    {
        // D3/M2 palette contract: Grass GREEN, Dirt BROWN (red-leaning, darker blue), Cobble WARM gray
        // (red >= blue, low spread), DungeonStone COLD gray (blue >= red, low spread), Water BLUE. Exact
        // tints are a feel call — the hue relations are what the graybox must not lose.
        var grass = AuthoredSurfaceVisuals.Albedo(SurfaceCategory.Grass);
        Assert.True(grass.G > grass.R && grass.G > grass.B);

        var dirt = AuthoredSurfaceVisuals.Albedo(SurfaceCategory.Dirt);
        Assert.True(dirt.R > dirt.G && dirt.G > dirt.B);

        var cobble = AuthoredSurfaceVisuals.Albedo(SurfaceCategory.Cobble);
        Assert.True(cobble.R >= cobble.G && cobble.G >= cobble.B);
        Assert.True(cobble.R - cobble.B < 0.15f); // still reads gray, not orange

        var stone = AuthoredSurfaceVisuals.Albedo(SurfaceCategory.DungeonStone);
        Assert.True(stone.B >= stone.G && stone.G >= stone.R);
        Assert.True(stone.B - stone.R < 0.15f); // still reads gray, not blue

        var water = AuthoredSurfaceVisuals.Albedo(SurfaceCategory.Water);
        Assert.True(water.B > water.G && water.G > water.R);
    }

    [Fact]
    public void AlbedoTintsArePairwiseDistinct()
    {
        var tints = new (float R, float G, float B)[AuthoredSurfaceVisuals.CategoryCount];
        for (var i = 0; i < tints.Length; i++)
        {
            tints[i] = AuthoredSurfaceVisuals.Albedo((SurfaceCategory)i);
        }

        for (var a = 0; a < tints.Length; a++)
        {
            for (var b = a + 1; b < tints.Length; b++)
            {
                Assert.NotEqual(tints[a], tints[b]);
            }
        }
    }

    [Fact]
    public void AlbedoThrowsOnUnknownCategory()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AuthoredSurfaceVisuals.Albedo((SurfaceCategory)99));
    }

    [Fact]
    public void WallBoxAlwaysDrawsOnNonAuthoredZones()
    {
        // genVersion 1 has no authored map: every blocked tile keeps its box, exactly the pre-M2 look.
        Assert.True(AuthoredSurfaceVisuals.ShouldDrawWallBox(authored: null, new TileCoord(0, 0)));
    }

    [Fact]
    public void WallBoxSkipsWaterAndOutOfWorldButNotWalls()
    {
        var map = Map();
        Assert.True(AuthoredSurfaceVisuals.ShouldDrawWallBox(map, new TileCoord(0, 0)));  // '#' wall → box
        Assert.False(AuthoredSurfaceVisuals.ShouldDrawWallBox(map, new TileCoord(2, 0))); // '~' water → flat blue only
        Assert.False(AuthoredSurfaceVisuals.ShouldDrawWallBox(map, new TileCoord(3, 0))); // ' ' out-of-world → nothing
    }

    [Fact]
    public void CollectChunkTilesGroupsByCategoryAndSkipsOutOfWorld()
    {
        var map = Map();
        var perCategory = AuthoredSurfaceVisuals.CollectChunkTiles(map, 0, 0, map.Width, map.Height);

        Assert.Equal(AuthoredSurfaceVisuals.CategoryCount, perCategory.Length);

        // Grass: '.'(1,0), plus the '#' wall (0,0) — painted under its box, see the under-box rationale —
        // and the H/T markers + '.' tiles on row 2. Row-major order is part of the contract (determinism).
        Assert.Equal(
            new[]
            {
                new TileCoord(0, 0), new TileCoord(1, 0),
                new TileCoord(0, 2), new TileCoord(1, 2), new TileCoord(2, 2), new TileCoord(3, 2),
            },
            perCategory[(int)SurfaceCategory.Grass]);

        Assert.Equal(new[] { new TileCoord(0, 1) }, perCategory[(int)SurfaceCategory.Dirt]);
        // Cobble: ':'(1,1) and the 'S' spawn anchor (3,1).
        Assert.Equal(new[] { new TileCoord(1, 1), new TileCoord(3, 1) }, perCategory[(int)SurfaceCategory.Cobble]);
        Assert.Equal(new[] { new TileCoord(2, 1) }, perCategory[(int)SurfaceCategory.DungeonStone]);
        Assert.Equal(new[] { new TileCoord(2, 0) }, perCategory[(int)SurfaceCategory.Water]);

        // The out-of-world padding tile (3,0) appears in NO category — no floor is painted there.
        var total = 0;
        foreach (var list in perCategory)
        {
            total += list.Count;
            Assert.DoesNotContain(new TileCoord(3, 0), list);
        }

        Assert.Equal((map.Width * map.Height) - 1, total);
    }

    [Fact]
    public void CollectChunkTilesHonoursHalfOpenBounds()
    {
        // The chunk range is [x0,x1) × [y0,y1): the 2×2 window at (1,0) covers '.', '~', ':', '-' only.
        var perCategory = AuthoredSurfaceVisuals.CollectChunkTiles(Map(), 1, 0, 3, 2);

        Assert.Equal(new[] { new TileCoord(1, 0) }, perCategory[(int)SurfaceCategory.Grass]);
        Assert.Equal(new[] { new TileCoord(2, 0) }, perCategory[(int)SurfaceCategory.Water]);
        Assert.Equal(new[] { new TileCoord(1, 1) }, perCategory[(int)SurfaceCategory.Cobble]);
        Assert.Equal(new[] { new TileCoord(2, 1) }, perCategory[(int)SurfaceCategory.DungeonStone]);
        Assert.Empty(perCategory[(int)SurfaceCategory.Dirt]);
    }
}
