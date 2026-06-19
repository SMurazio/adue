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
/// </summary>
public static class TerrainGenerator
{
    /// <summary>The algorithm version the server currently generates with.</summary>
    public const int CurrentGenVersion = 1;

    // genVersion 1 reproduces the historical hand-authored map exactly: a 1-tile blocked border around
    // the whole world plus three short interior wall segments, with the legacy default spawn tile
    // carved back open. The seed is plumbed through a deterministic PRNG but genVersion 1 does not yet
    // consume randomness (the layout is fixed); the PRNG exists so a future genVersion can scatter
    // obstacles deterministically without a wire change.
    private static readonly TileCoord LegacyDefaultSpawnTile = new(8, 8);

    /// <summary>
    /// Generates the blocked-tile set for a zone. Returns tiles in a fixed (row-major, then sorted)
    /// order so callers that hash or compare the sequence get identical results everywhere.
    /// </summary>
    public static IReadOnlyList<TileCoord> Generate(int width, int height, int seed, int genVersion)
    {
        if (width < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Width must be positive.");
        }

        if (height < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(height), "Height must be positive.");
        }

        if (genVersion != 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(genVersion),
                $"Unsupported terrain genVersion {genVersion}. This build generates version {CurrentGenVersion}.");
        }

        return GenerateVersion1(width, height, seed);
    }

    /// <summary>
    /// Stable 64-bit FNV-1a hash of the generated blocked set. Order-independent inputs are made stable
    /// by hashing the canonically-ordered tile list produced by <see cref="Generate"/>. Used as a
    /// drift/tamper check: server and client compare hashes and the server stays authoritative either way.
    /// </summary>
    public static ulong ContentHash(int width, int height, int seed, int genVersion)
    {
        return ContentHash(Generate(width, height, seed, genVersion));
    }

    /// <summary>
    /// Stable 64-bit FNV-1a hash over an ordered tile sequence. Includes the count and each tile's
    /// (X, Y) so two different layouts cannot collide trivially. Callers must pass tiles in the
    /// canonical order <see cref="Generate"/> emits.
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
