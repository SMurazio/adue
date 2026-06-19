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

    // The map is content, not state: the server builds its authoritative TileGrid from the same shared
    // deterministic generator the clients use, so it never has to ship the blocked-tile list. The
    // historical "default" map is genVersion 1 with a fixed default seed (overload below).
    public static TileGrid CreateDefault(int width, int height)
    {
        return CreateGenerated(width, height, DefaultSeed, TerrainGenerator.CurrentGenVersion);
    }

    public static TileGrid CreateGenerated(int width, int height, int seed, int genVersion)
    {
        var blocked = TerrainGenerator.Generate(width, height, seed, genVersion);
        return new TileGrid(width, height, blocked);
    }

    /// <summary>Stable default seed so the generated map (and persisted tile positions) survive restarts.</summary>
    public const int DefaultSeed = 0;

    public bool IsInBounds(TileCoord tile)
    {
        return tile.X >= 0 && tile.X < Width && tile.Y >= 0 && tile.Y < Height;
    }

    public bool IsWalkable(TileCoord tile)
    {
        return IsInBounds(tile) && !_blockedTiles.Contains(tile);
    }
}
