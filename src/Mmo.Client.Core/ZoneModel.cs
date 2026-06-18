using Mmo.Shared.Domain;

namespace Mmo.Client.Core;

public sealed class ZoneModel
{
    private readonly HashSet<TileCoord> _blockedTiles;

    public ZoneModel(string zoneId, int width, int height, IEnumerable<TileCoord> blockedTiles)
    {
        ZoneId = zoneId;
        Width = width;
        Height = height;
        _blockedTiles = new HashSet<TileCoord>(blockedTiles);
    }

    public string ZoneId { get; }

    public int Width { get; }

    public int Height { get; }

    public IReadOnlySet<TileCoord> BlockedTiles => _blockedTiles;

    public bool IsBlocked(TileCoord tile)
    {
        return _blockedTiles.Contains(tile);
    }
}
