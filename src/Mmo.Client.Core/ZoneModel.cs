using Mmo.Shared.Domain;

namespace Mmo.Client.Core;

// Terrain is procedural content: the client regenerates the blocked-tile set locally from the
// (Width, Height, Seed, GenVersion) descriptor in ZoneInfo via the same shared deterministic
// TerrainGenerator the server uses, instead of receiving a tile payload. ContentHash is the locally
// computed hash; callers compare it to the server's ContentHash as a drift/tamper check.
// AUTHORED-MAP M1: regeneration now yields the FULL TerrainLayout, so for authored genVersions the
// client also gets surface categories / spawn anchors / prop markers (the terrain painter's M2 input)
// and — critically — the layout's canonical ContentHash, which for authored maps covers categories
// and markers too, not just the blocked set (a category-only drift must hard-fail the same way).
public sealed class ZoneModel
{
    private readonly HashSet<TileCoord> _blockedTiles;
    private readonly TerrainLayout _layout;

    public ZoneModel(string zoneId, int width, int height, int seed, int genVersion)
    {
        ZoneId = zoneId;
        Width = width;
        Height = height;
        Seed = seed;
        GenVersion = genVersion;

        _layout = TerrainGenerator.GenerateLayout(width, height, seed, genVersion);
        _blockedTiles = new HashSet<TileCoord>(_layout.BlockedTiles);
        ContentHash = _layout.ContentHash;
    }

    public string ZoneId { get; }

    public int Width { get; }

    public int Height { get; }

    public int Seed { get; }

    public int GenVersion { get; }

    /// <summary>Locally computed canonical layout hash; compare to the server's ContentHash.</summary>
    public ulong ContentHash { get; }

    public IReadOnlySet<TileCoord> BlockedTiles => _blockedTiles;

    /// <summary>The parsed authored map (categories/spawn anchors/markers), or null when not authored.</summary>
    public AuthoredMap? Authored => _layout.Authored;

    /// <summary>Surface category at a tile; Grass everywhere on non-authored (genVersion 1) maps.</summary>
    public SurfaceCategory CategoryAt(TileCoord tile)
    {
        return _layout.CategoryAt(tile);
    }

    public bool IsBlocked(TileCoord tile)
    {
        return _blockedTiles.Contains(tile);
    }
}
