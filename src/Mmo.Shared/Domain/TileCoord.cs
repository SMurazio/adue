namespace Mmo.Shared.Domain;

public readonly record struct TileCoord(int X, int Y)
{
    public static readonly TileCoord Zero = new(0, 0);

    public TileCoord Offset(int dx, int dy)
    {
        return new TileCoord(X + dx, Y + dy);
    }
}
