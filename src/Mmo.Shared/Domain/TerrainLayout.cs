namespace Mmo.Shared.Domain;

/// <summary>
/// The full result of terrain generation — what <see cref="TerrainGenerator.GenerateLayout"/> returns.
/// Wraps the blocked-tile set (all any caller had before authored maps existed) together with the
/// layout's canonical <see cref="ContentHash"/> and, for authored genVersions, the parsed
/// <see cref="AuthoredMap"/> (surface categories, spawn anchors, prop markers).
///
/// <see cref="Authored"/> is null for procedural genVersions (genVersion 1): those layouts have no
/// authored data, and the accessors below fall back to the defaults every pre-authored caller already
/// assumed — <see cref="SurfaceCategory.Grass"/> everywhere, no spawn anchors, no markers. Callers
/// that only care about collision keep reading <see cref="BlockedTiles"/> exactly as before.
///
/// ContentHash note: this hash is THE drift/tamper check value for the layout. For genVersion 1 it is
/// byte-identical to the historical blocked-only hash; for authored versions it additionally covers
/// categories/spawns/markers/out-of-world (see <see cref="TerrainGenerator.ContentHash(AuthoredMap)"/>),
/// so a category-only edit — which leaves the blocked set untouched — still hard-fails a stale peer.
/// Always compare THIS value, never re-hash the blocked list on an authored layout.
/// </summary>
public sealed class TerrainLayout
{
    public TerrainLayout(IReadOnlyList<TileCoord> blockedTiles, ulong contentHash, AuthoredMap? authored)
    {
        ArgumentNullException.ThrowIfNull(blockedTiles);
        BlockedTiles = blockedTiles;
        ContentHash = contentHash;
        Authored = authored;
    }

    /// <summary>Blocked tiles in the generator's canonical row-major order.</summary>
    public IReadOnlyList<TileCoord> BlockedTiles { get; }

    /// <summary>The layout's canonical drift-check hash (see class remarks).</summary>
    public ulong ContentHash { get; }

    /// <summary>The parsed authored map, or null for procedural genVersions (genVersion 1).</summary>
    public AuthoredMap? Authored { get; }

    /// <summary>Surface category at a tile; <see cref="SurfaceCategory.Grass"/> everywhere when not authored.</summary>
    public SurfaceCategory CategoryAt(TileCoord tile)
    {
        return Authored?.CategoryAt(tile) ?? SurfaceCategory.Grass;
    }

    /// <summary>Authored `S` spawn anchor tiles; empty when not authored.</summary>
    public IReadOnlyList<TileCoord> SpawnTiles => Authored?.SpawnTiles ?? [];

    /// <summary>Authored prop markers; empty when not authored.</summary>
    public IReadOnlyList<AuthoredMarker> Markers => Authored?.Markers ?? [];
}
