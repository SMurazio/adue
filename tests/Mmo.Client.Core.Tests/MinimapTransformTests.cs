using Mmo.Client.Core;
using Xunit;

namespace Mmo.Client.Core.Tests;

// TRANSFORM FIX (review/review-request-minimap-transform-fix.md): pins the minimap's world->widget
// PLACEMENT math with the live repro-B numbers — the 384x384 authored world, the player standing on tile
// (19,24) in the far corner (= continuous (19.5, 24.5)), minimum zoom 3, and the 172px inner viewport
// (PanelSize 200 - 2 * FrameInset 14). The bake-side tests (MinimapAuthoredPaletteTests /
// MinimapRasterBytesTests) were green while the live minimap drew the map wildly misplaced, because they
// only cover WHAT color a tile bakes to; these cover WHERE the map is drawn, so a "colors right, placement
// wrong" regression can never again ship silently.
public sealed class MinimapTransformTests
{
    private const int MapSize = 384;   // the live authored world (AuthoredMaps.TownAndFloor1Width/Height).
    private const float Inner = 172f;  // minimap clip viewport side: PanelSize 200 - 2 * FrameInset 14.
    private const float Centre = Inner / 2f;

    // Repro B: the player stood ON tile (19,24) — continuous tile-space (19.5, 24.5).
    private const float PlayerX = 19.5f;
    private const float PlayerY = 24.5f;

    [Fact]
    public void DisplayRectSizeIsExactlyMapDimsTimesScale()
    {
        // THE shipped-bug invariant: the drawn rect's size must be EXACTLY map-dims * scale — the live bug
        // rendered it as (Width*scale - prevOffset.x, Height*scale - prevOffset.y), a player-position-
        // dependent stretch. Size must depend on nothing but the map dims and the zoom.
        var (x, y, w, h) = MinimapTransform.DisplayRect(MapSize, MapSize, 3, PlayerX, PlayerY, Inner);

        Assert.Equal(384f * 3f, w);
        Assert.Equal(384f * 3f, h);
        Assert.Equal(Centre - (PlayerX * 3f), x); // 86 - 58.5 = 27.5
        Assert.Equal(Centre - (PlayerY * 3f), y); // 86 - 73.5 = 12.5
    }

    [Fact]
    public void DisplayRectAtDefaultZoomMatchesTheIntendedRect()
    {
        // Same invariant at the default zoom (6): position (86 - 19.5*6, 86 - 24.5*6), size 384*6 exactly.
        var (x, y, w, h) = MinimapTransform.DisplayRect(MapSize, MapSize, 6, PlayerX, PlayerY, Inner);

        Assert.Equal(2304f, w);
        Assert.Equal(2304f, h);
        Assert.Equal(-31f, x);
        Assert.Equal(-61f, y);
    }

    [Fact]
    public void PlayerTileLandsAtTheWidgetCentre()
    {
        // Repro B pinned: standing on tile (19,24), that tile's centre pixel must sit exactly under the
        // arrow at the viewport centre (86, 86) — at the zoom the screenshot was taken at (3).
        var (px, py) = MinimapTransform.TileCentrePixel(19, 24, 3, PlayerX, PlayerY, Inner);

        Assert.Equal(Centre, px);
        Assert.Equal(Centre, py);
    }

    [Fact]
    public void PlayerTileStaysAtTheWidgetCentreAtEveryZoom()
    {
        // A zoom click changes ONLY the pixels-per-tile, never what sits under the arrow (the live +/-
        // buttons walk 3..16). This is the invariant a re-bake-free zoom must preserve.
        for (var scale = 3; scale <= 16; scale++)
        {
            var (px, py) = MinimapTransform.TileCentrePixel(19, 24, scale, PlayerX, PlayerY, Inner);

            Assert.Equal(Centre, px);
            Assert.Equal(Centre, py);
        }
    }

    [Fact]
    public void TownCentreTileLandsFarOffTheWidget()
    {
        // Repro B's decisive check: the town's spawn plaza is tile (194,38) — 150+ tiles east of the
        // repro player. Its pixel must land far OFF the widget (well over 86px from the centre, and past
        // the 172px viewport edge). The live bug put town-region content under the arrow instead.
        var (px, py) = MinimapTransform.TileCentrePixel(194, 38, 3, PlayerX, PlayerY, Inner);

        Assert.Equal(611f, px); // (194.5 * 3) + 27.5 — ~440px past the viewport's right edge.
        Assert.Equal(128f, py); // (38.5 * 3) + 12.5 — inside vertically; x alone must exile it.
        Assert.True(px > Inner, $"town centre must be past the {Inner}px viewport edge, was {px}");

        var dx = px - Centre;
        var dy = py - Centre;
        Assert.True((dx * dx) + (dy * dy) > 86f * 86f,
            $"town centre must be >86px from the widget centre, was ({px},{py})");
    }

    [Fact]
    public void MapObjectsAndArrowShareOneTransform()
    {
        // The baked map's rect position, the object layer's offset, and the arrow anchoring must all be the
        // SAME translation: DisplayRect's position == MapOffset, and a world object exactly at the player's
        // continuous position draws dead-centre under the arrow.
        var (ox, oy) = MinimapTransform.MapOffset(3, PlayerX, PlayerY, Inner);
        var rect = MinimapTransform.DisplayRect(MapSize, MapSize, 3, PlayerX, PlayerY, Inner);

        Assert.Equal(ox, rect.X);
        Assert.Equal(oy, rect.Y);

        var (wx, wy) = MinimapTransform.WorldPixel(PlayerX, PlayerY, 3, PlayerX, PlayerY, Inner);
        Assert.Equal(Centre, wx);
        Assert.Equal(Centre, wy);
    }

    [Fact]
    public void DegenerateMapDimsClampToOneTile()
    {
        // Mirrors the live code's Max(1, dims) guard: a zero/negative-dims map still yields a drawable rect.
        var rect = MinimapTransform.DisplayRect(0, -5, 4, 0.5f, 0.5f, Inner);

        Assert.Equal(4f, rect.Width);
        Assert.Equal(4f, rect.Height);
    }
}
