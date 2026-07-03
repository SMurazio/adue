namespace Mmo.Client.Core;

// TRANSFORM FIX (live repro, review/review-request-minimap-transform-fix.md): the minimap's ONE
// world->widget transform as pure, Godot-free math. Minimap.cs consumes these for the baked map's display
// rect AND the object-layer/player translation; MinimapTransformTests pins them with the live-repro
// numbers. The bug this closes was exactly the gap between the existing tests and the screen: the bake
// helpers (MinimapAuthoredPalette / MinimapRasterBytes) are headlessly tested for WHAT color a tile bakes
// to, but nothing covered WHERE the baked texture is drawn — the placement lived as two side-effectful
// Godot offset writes that silently disagreed with each other (see Minimap.ApplyDisplayRect for the full
// root cause). Colors right + placement wrong must never again be able to ship without a red test.
//
// Conventions (all parameters in the same units the live code uses):
//   * playerX/playerY are the local player's CONTINUOUS tile-space coords — a player standing on tile
//     (19,24) is at (19.5, 24.5). They multiply by the scale DIRECTLY; the +0.5 tile-centre shift belongs
//     only to integer tile INDICES (TileCentrePixel), never to an already-continuous coordinate.
//   * mapScale is the live zoom in pixels per tile; innerSize is the square clip viewport's side in px
//     (the minimap panel minus its frame border).
//   * Widget pixels: origin at the clip viewport's top-left, +x right, +y down; the player is pinned at
//     the viewport centre (innerSize/2, innerSize/2), the map translates underneath.
public static class MinimapTransform
{
    // The player-centred translation: where the baked map's (0,0) pixel sits in widget space so that the
    // player's continuous position lands exactly at the viewport centre. This SAME offset must be used for
    // the baked map and the object layer — one formula, no drift.
    public static (float X, float Y) MapOffset(int mapScale, float playerX, float playerY, float innerSize)
    {
        return (
            (innerSize / 2f) - (playerX * mapScale),
            (innerSize / 2f) - (playerY * mapScale));
    }

    // The full rect the baked whole-map texture must be drawn into at the current zoom. The size is
    // EXACTLY (mapWidth * mapScale, mapHeight * mapScale) — the invariant the shipped bug violated — and
    // the position is MapOffset. The caller must write BOTH onto the control every time, together.
    public static (float X, float Y, float Width, float Height) DisplayRect(
        int mapWidth, int mapHeight, int mapScale, float playerX, float playerY, float innerSize)
    {
        var (x, y) = MapOffset(mapScale, playerX, playerY, innerSize);
        return (x, y, Math.Max(1, mapWidth) * mapScale, Math.Max(1, mapHeight) * mapScale);
    }

    // Widget pixel of an arbitrary CONTINUOUS world position (an object square's centre, another player).
    public static (float X, float Y) WorldPixel(
        float worldX, float worldY, int mapScale, float playerX, float playerY, float innerSize)
    {
        var (offsetX, offsetY) = MapOffset(mapScale, playerX, playerY, innerSize);
        return ((worldX * mapScale) + offsetX, (worldY * mapScale) + offsetY);
    }

    // Widget pixel of an integer TILE's centre: tile t spans [t, t+1) in continuous space, so its centre
    // is t + 0.5. This is the ONLY place the +0.5 lives.
    public static (float X, float Y) TileCentrePixel(
        int tileX, int tileY, int mapScale, float playerX, float playerY, float innerSize)
    {
        return WorldPixel(tileX + 0.5f, tileY + 0.5f, mapScale, playerX, playerY, innerSize);
    }
}
