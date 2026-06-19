using Mmo.Shared.Domain;

namespace Mmo.Client.Core;

// Terrain is procedural content: the client regenerates the blocked-tile set locally from the
// (Width, Height, Seed, GenVersion) descriptor in ZoneInfo via the same shared deterministic
// TerrainGenerator the server uses, instead of receiving a tile payload. ContentHash is the locally
// computed hash; callers compare it to the server's ContentHash as a drift/tamper check.
public sealed class ZoneModel
{
    private readonly HashSet<TileCoord> _blockedTiles;

    public ZoneModel(string zoneId, int width, int height, int seed, int genVersion)
    {
        ZoneId = zoneId;
        Width = width;
        Height = height;
        Seed = seed;
        GenVersion = genVersion;

        var blocked = TerrainGenerator.Generate(width, height, seed, genVersion);
        _blockedTiles = new HashSet<TileCoord>(blocked);
        ContentHash = TerrainGenerator.ContentHash(blocked);
    }

    public string ZoneId { get; }

    public int Width { get; }

    public int Height { get; }

    public int Seed { get; }

    public int GenVersion { get; }

    /// <summary>FNV-1a hash of the locally regenerated blocked set; compare to the server's ContentHash.</summary>
    public ulong ContentHash { get; }

    public IReadOnlySet<TileCoord> BlockedTiles => _blockedTiles;

    public bool IsBlocked(TileCoord tile)
    {
        return _blockedTiles.Contains(tile);
    }
}
