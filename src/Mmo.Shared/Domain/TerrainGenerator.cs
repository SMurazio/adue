namespace Mmo.Shared.Domain;

/// <summary>
/// Shared, deterministic procedural terrain generator. Given <c>(width, height, seed, genVersion)</c>
/// it produces a byte-identical set of blocked tiles on both the server and every client, so static
/// terrain ships as a tiny descriptor (the seed) instead of a full blocked-tile payload.
///
/// Determinism is the contract: identical inputs MUST yield byte-identical output regardless of
/// platform, culture, or runtime. Concretely:
///  * No <see cref="System.Random"/> without an explicit seed, no clocks, no culture-sensitive APIs.
///  * Iteration order is fixed (we emit tiles in row-major order, then sort defensively).
///  * The PRNG is a fixed, self-contained integer algorithm (SplitMix64) so it cannot drift with the
///    framework's RNG implementation.
///
/// <paramref name="genVersion"/> selects the algorithm. Bumping it lets the layout change later
/// without a silent client/server mismatch — old and new clients simply disagree on the hash and log
/// loudly. <see cref="CurrentGenVersion"/> is what the server emits today.
///
/// genVersion 2 (AUTHORED, town-blockout D1): the layout is not procedural at all — it is the shared
/// hand-authored ASCII grid <see cref="AuthoredMaps.TownAndFloor1"/>, parsed by <see cref="AuthoredMap"/>
/// into blocked set + surface categories + spawn anchors + prop markers. Determinism is trivially the
/// parse of a compiled-in constant (the seed is intentionally UNUSED), and the ContentHash covers the
/// ENTIRE authored layout — categories/spawns/markers too, not just walls — so a category-only edit
/// still hard-fails a stale peer. Callers that need more than the blocked set use
/// <see cref="GenerateLayout"/>; the older <see cref="Generate"/> stays as a blocked-only view.
/// </summary>
public static class TerrainGenerator
{
    /// <summary>
    /// The algorithm version the server boots with by default. M3 flipped this to the authored
    /// town+floor-1 map (<see cref="AuthoredGenVersion"/>); genVersion 1 — the procedural
    /// border+segments layout — remains fully generatable for old tests and the MMO_GEN_VERSION=1
    /// escape hatch (an authored genVersion requires the world dims to match the authored grid).
    /// </summary>
    public const int CurrentGenVersion = 2;

    /// <summary>The authored-map version (town+floor-1 blockout) — the server default as of M3.</summary>
    public const int AuthoredGenVersion = 2;

    // genVersion 1 reproduces the historical hand-authored map exactly: a 1-tile blocked border around
    // the whole world plus three short interior wall segments, with the legacy default spawn tile
    // carved back open. The seed is plumbed through a deterministic PRNG but genVersion 1 does not yet
    // consume randomness (the layout is fixed); the PRNG exists so a future genVersion can scatter
    // obstacles deterministically without a wire change.
    private static readonly TileCoord LegacyDefaultSpawnTile = new(8, 8);

    /// <summary>
    /// Generates the blocked-tile set for a zone. Returns tiles in a fixed (row-major, then sorted)
    /// order so callers that hash or compare the sequence get identical results everywhere.
    /// Blocked-only view of <see cref="GenerateLayout"/> — collision-only callers keep using this.
    /// </summary>
    public static IReadOnlyList<TileCoord> Generate(int width, int height, int seed, int genVersion)
    {
        return GenerateLayout(width, height, seed, genVersion).BlockedTiles;
    }

    /// <summary>
    /// Generates the FULL layout for a zone: blocked tiles, the layout's canonical ContentHash, and —
    /// for authored genVersions — the parsed <see cref="AuthoredMap"/> (surface categories, spawn
    /// anchors, prop markers; null for genVersion 1, where every accessor falls back to the historical
    /// defaults). The one generation entry point; <see cref="Generate"/> and the (width, height, seed,
    /// genVersion) ContentHash overload are thin views over it, so every caller — server Zone, client
    /// ZoneModel, web bridge — derives blocked set AND hash from the same computation.
    /// </summary>
    public static TerrainLayout GenerateLayout(int width, int height, int seed, int genVersion)
    {
        if (width < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Width must be positive.");
        }

        if (height < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(height), "Height must be positive.");
        }

        if (genVersion == 1)
        {
            var blocked = GenerateVersion1(width, height, seed);
            // genVersion 1 hash = the historical blocked-only hash, byte-identical to every build that
            // shipped before authored maps existed (pinned by test). No authored data.
            return new TerrainLayout(blocked, ContentHash(blocked), authored: null);
        }

        if (genVersion == AuthoredGenVersion)
        {
            return GenerateVersion2(width, height);
        }

        throw new ArgumentOutOfRangeException(
            nameof(genVersion),
            $"Unsupported terrain genVersion {genVersion}. This build generates versions 1 (procedural) and {AuthoredGenVersion} (authored).");
    }

    /// <summary>
    /// Stable 64-bit FNV-1a hash of the generated layout — THE drift/tamper check value: server and
    /// client compare hashes and the server stays authoritative either way. For genVersion 1 this is
    /// the historical blocked-only hash; for authored versions it covers the whole authored layout.
    /// </summary>
    public static ulong ContentHash(int width, int height, int seed, int genVersion)
    {
        return GenerateLayout(width, height, seed, genVersion).ContentHash;
    }

    /// <summary>
    /// Stable 64-bit FNV-1a hash over an ordered tile sequence. Includes the count and each tile's
    /// (X, Y) so two different layouts cannot collide trivially. Callers must pass tiles in the
    /// canonical order <see cref="Generate"/> emits. NOTE: for an AUTHORED layout this blocked-only
    /// hash is NOT the layout's ContentHash (which also covers categories/spawns/markers) — compare
    /// <see cref="TerrainLayout.ContentHash"/>, never a re-hash of the blocked list.
    /// </summary>
    public static ulong ContentHash(IReadOnlyList<TileCoord> blockedTiles)
    {
        ArgumentNullException.ThrowIfNull(blockedTiles);

        const ulong fnvOffset = 14695981039346656037UL;
        const ulong fnvPrime = 1099511628211UL;

        var hash = fnvOffset;
        hash = MixInt32(hash, fnvPrime, blockedTiles.Count);
        foreach (var tile in blockedTiles)
        {
            hash = MixInt32(hash, fnvPrime, tile.X);
            hash = MixInt32(hash, fnvPrime, tile.Y);
        }

        return hash;
    }

    /// <summary>
    /// Stable 64-bit FNV-1a hash of a FULL authored map — the genVersion 2+ ContentHash. One FNV-1a
    /// chain over, in this fixed canonical order:
    ///   1. the blocked set exactly as <see cref="ContentHash(IReadOnlyList{TileCoord})"/> hashes it
    ///      (count, then each tile's X, Y — the historical wall-geometry chain),
    ///   2. Width, Height,
    ///   3. every tile's <see cref="SurfaceCategory"/> byte in row-major order,
    ///   4. spawn-anchor count, then each anchor's X, Y (row-major),
    ///   5. marker count, then each marker's kind byte, X, Y (row-major),
    ///   6. out-of-world count, then each tile's X, Y (row-major).
    /// Covering 2-6 (not just walls) is the point: an authored edit that ONLY recolors a tile or moves
    /// a marker leaves the blocked set untouched, and a blocked-only hash would let a stale client
    /// render the wrong world silently. Every list is emitted row-major by the parser, so the chain is
    /// platform/culture-independent like everything else here. This order is a compatibility contract:
    /// changing it invalidates every shipped authored map's hash.
    /// </summary>
    public static ulong ContentHash(AuthoredMap map)
    {
        ArgumentNullException.ThrowIfNull(map);

        const ulong fnvPrime = 1099511628211UL;

        var hash = ContentHash(map.BlockedTiles);
        hash = MixInt32(hash, fnvPrime, map.Width);
        hash = MixInt32(hash, fnvPrime, map.Height);

        for (var y = 0; y < map.Height; y++)
        {
            for (var x = 0; x < map.Width; x++)
            {
                hash = MixByte(hash, fnvPrime, (byte)map.CategoryAt(x, y));
            }
        }

        hash = MixInt32(hash, fnvPrime, map.SpawnTiles.Count);
        foreach (var tile in map.SpawnTiles)
        {
            hash = MixInt32(hash, fnvPrime, tile.X);
            hash = MixInt32(hash, fnvPrime, tile.Y);
        }

        hash = MixInt32(hash, fnvPrime, map.Markers.Count);
        foreach (var marker in map.Markers)
        {
            hash = MixByte(hash, fnvPrime, (byte)marker.Kind);
            hash = MixInt32(hash, fnvPrime, marker.Tile.X);
            hash = MixInt32(hash, fnvPrime, marker.Tile.Y);
        }

        hash = MixInt32(hash, fnvPrime, map.OutOfWorldTiles.Count);
        foreach (var tile in map.OutOfWorldTiles)
        {
            hash = MixInt32(hash, fnvPrime, tile.X);
            hash = MixInt32(hash, fnvPrime, tile.Y);
        }

        return hash;
    }

    private static ulong MixInt32(ulong hash, ulong prime, int value)
    {
        var unsigned = (uint)value;
        for (var shift = 0; shift < 32; shift += 8)
        {
            hash ^= (byte)(unsigned >> shift);
            hash *= prime;
        }

        return hash;
    }

    private static ulong MixByte(ulong hash, ulong prime, byte value)
    {
        hash ^= value;
        hash *= prime;
        return hash;
    }

    private static IReadOnlyList<TileCoord> GenerateVersion1(int width, int height, int seed)
    {
        // Touch the PRNG so the seed is wired through deterministically even though the v1 layout is
        // fixed; keeps the contract (seed in -> deterministic out) honest and ready for v2.
        _ = NextUInt64(SeedState(seed));

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
        blocked.Remove(LegacyDefaultSpawnTile);

        // Canonical order: row-major (Y, then X). Deterministic and platform-independent.
        var ordered = new List<TileCoord>(blocked.Count);
        ordered.AddRange(blocked);
        ordered.Sort(static (a, b) => a.Y != b.Y ? a.Y.CompareTo(b.Y) : a.X.CompareTo(b.X));
        return ordered;
    }

    // M3-REVIEW-FOLLOWUPS item 4: the authored map is a single COMPILED-IN constant — parsing + hashing it is
    // pure and produces the IDENTICAL result every call, so cache it ONCE per process instead of re-parsing all
    // ~147,456 tiles on every call (GameServer.CreateZoneInfoMessage → TerrainGenerator.ContentHash →
    // GenerateLayout → here, on EVERY login — single-digit ms per call, fine at today's traffic, but pointless
    // repeated work). Lazy<T> is thread-safe by default (ExecutionAndPublication), so concurrent first-callers
    // (e.g. two logins racing the very first call) still only parse once.
    private static readonly Lazy<TerrainLayout> AuthoredLayoutCache = new(() =>
    {
        var map = AuthoredMap.Parse(AuthoredMaps.TownAndFloor1);
        return new TerrainLayout(map.BlockedTiles, ContentHash(map), map);
    });

    // genVersion 2 (AUTHORED): the layout IS the shared ASCII grid — served from AuthoredLayoutCache. The seed is
    // intentionally unused (an authored map has no randomness to seed) and the caller's (width, height) MUST
    // match the authored grid's intrinsic dimensions: ZoneInfo carries dimensions on the wire, so a server
    // configured with the wrong size would otherwise generate a world that disagrees with its own content — fail
    // loudly here (boot/test), same as before caching; only the source of the dimensions to check against
    // (the cached parse's own Width/Height, instead of a fresh one) changed.
    private static TerrainLayout GenerateVersion2(int width, int height)
    {
        var layout = AuthoredLayoutCache.Value;
        var map = layout.Authored!; // always populated by the cache factory above.
        if (width != map.Width || height != map.Height)
        {
            throw new ArgumentOutOfRangeException(
                nameof(width),
                $"genVersion {AuthoredGenVersion} is the authored {map.Width}x{map.Height} map; " +
                $"requested {width}x{height}. Configure the world size to match the authored content.");
        }

        return layout;
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

    // SplitMix64: a tiny, self-contained, fully-specified integer PRNG. Identical output on every
    // platform/runtime (pure 64-bit unsigned arithmetic with defined overflow), so it cannot drift the
    // way a framework RNG might. State seeding folds the 32-bit seed into 64 bits deterministically.
    private static ulong SeedState(int seed)
    {
        return (ulong)(uint)seed * 0x9E3779B97F4A7C15UL;
    }

    private static ulong NextUInt64(ulong state)
    {
        unchecked
        {
            state += 0x9E3779B97F4A7C15UL;
            var z = state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }
}
