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

    public static RenderPosition Lerp(RenderPosition from, RenderPosition to, double alpha)
    {
        var clamped = Math.Clamp(alpha, 0d, 1d);
        return new RenderPosition(
            from.X + ((to.X - from.X) * clamped),
            from.Y + ((to.Y - from.Y) * clamped));
    }
}
