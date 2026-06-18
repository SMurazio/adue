using Mmo.Shared.Domain;

namespace Mmo.Server.Runtime;

public sealed class TileGrid
{
    private readonly HashSet<TileCoord> _blockedTiles;

    public TileGrid(int width, int height, IEnumerable<TileCoord> blockedTiles)
    {
        if (width < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Width must be positive.");
        }

        if (height < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(height), "Height must be positive.");
        }

        Width = width;
        Height = height;
        _blockedTiles = blockedTiles
            .Where(IsInBounds)
            .ToHashSet();
    }

    public static TileCoord DefaultSpawnTile { get; } = new(8, 8);

    public int Width { get; }
    public int Height { get; }
    public IReadOnlySet<TileCoord> BlockedTiles => _blockedTiles;

    public static TileGrid CreateDefault(int width, int height)
    {
        var blocked = new HashSet<TileCoord>();

        for (var x = 0; x < width; x++)
        {
            blocked.Add(new TileCoord(x, 0));
            blocked.Add(new TileCoord(x, height - 1));
        }

        for (var y = 0; y < height; y++)
        {
            blocked.Add(new TileCoord(0, y));
            blocked.Add(new TileCoord(width - 1, y));
        }

        AddVerticalSegment(blocked, width, height, 16, 8, 20);
        AddHorizontalSegment(blocked, width, height, 24, 20, 36);
        AddVerticalSegment(blocked, width, height, 40, 12, 18);
        blocked.Remove(DefaultSpawnTile);

        return new TileGrid(width, height, blocked);
    }

    public bool IsInBounds(TileCoord tile)
    {
        return tile.X >= 0 && tile.X < Width && tile.Y >= 0 && tile.Y < Height;
    }

    public bool IsWalkable(TileCoord tile)
    {
        return IsInBounds(tile) && !_blockedTiles.Contains(tile);
    }

    private static void AddVerticalSegment(HashSet<TileCoord> blocked, int width, int height, int x, int yStart, int yEnd)
    {
        for (var y = yStart; y <= yEnd; y++)
        {
            AddIfInBounds(blocked, width, height, new TileCoord(x, y));
        }
    }

    private static void AddHorizontalSegment(HashSet<TileCoord> blocked, int width, int height, int y, int xStart, int xEnd)
    {
        for (var x = xStart; x <= xEnd; x++)
        {
            AddIfInBounds(blocked, width, height, new TileCoord(x, y));
        }
    }

    private static void AddIfInBounds(HashSet<TileCoord> blocked, int width, int height, TileCoord tile)
    {
        if (tile.X >= 0 && tile.X < width && tile.Y >= 0 && tile.Y < height)
        {
            blocked.Add(tile);
        }
    }
}
