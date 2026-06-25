namespace Mmo.Client.Core;

public readonly record struct RenderPosition(double X, double Y)
{
    public static RenderPosition FromTile(int x, int y)
    {
        return new RenderPosition(x, y);
    }

    public static RenderPosition FromTile(Mmo.Shared.Domain.TileCoord tile)
    {
        return new RenderPosition(tile.X, tile.Y);
    }

    // CONTINUOUS MIGRATION (Phase 3, v36): render straight off the decoded continuous WorldVector. The Phase-3
    // client renders RAW (no prediction/interpolation) — the snapshot position is fed here every frame. WorldVector
    // is (X, Y) in tile units (1.0 == one tile), exactly the RenderPosition convention, so it maps 1:1.
    public static RenderPosition FromWorld(Mmo.Shared.Domain.WorldVector position)
    {
        return new RenderPosition(position.X, position.Y);
    }

    public static RenderPosition Lerp(RenderPosition from, RenderPosition to, double alpha)
    {
        var clamped = Math.Clamp(alpha, 0d, 1d);
        return new RenderPosition(
            from.X + ((to.X - from.X) * clamped),
            from.Y + ((to.Y - from.Y) * clamped));
    }
}
