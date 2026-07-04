using Mmo.Shared.Domain;
using Mmo.Shared.Domain.Population;

namespace Mmo.Server.Runtime;

// ECOLOGY E2 (docs/ecology-v1-design.md §3/§8 E2; docs/procedural-population-design.md D5): DERIVES each
// region×type's spawnTiles at boot instead of hand-authoring them in ecology.json (population D5 supersedes
// ecology D4's "explicit authored tiles" — the orchestrator's call, per this task's brief). Pure, static,
// headlessly testable: no Zone/GameServer dependency, only the shared P1 math (WeightedScatter,
// TileDistanceField, ValueNoise) the client's DecorPlacer already proved out for the SAME "away-from-road x
// noise" shape (D2 "civilization suppresses wilderness" applies to monster geography exactly as it does to
// decor density).
//
// FORK (flagged per the task brief): this duplicates DecorPlacer's road-distance BFS + curve math as a SEPARATE
// server-side computation from the client's own copy (Mmo.Client.Core.Population.DecorPlacer). They are
// deliberately NOT shared — client decor is L1 (client-only, zero wire, D1) and this is server-only ecology
// content; the two processes have no reason to agree on a distance field byte-for-byte, and sharing would
// create a cross-assembly dependency for zero benefit. Duplication across separate processes is fine (noted,
// not fixed, per the task brief).
public static class RegionSpawnPlanner
{
    // D5 "min spacing ~4" (verbatim from the design brief).
    public const int MinSpacing = 4;

    // "count: enough to host maxLive at overgrowth (ceil(1.5*maxLive) + slack)". Slack is a flat +4 tiles so
    // even a tiny region (maxLive as low as 1) still has a few alternate tiles for the "skip if a player is
    // within 6u" rule to fall back on, and the round-robin cursor doesn't just cycle 1-2 fixed spots. Reuses
    // EcologyState.OvergrowthCapMultiplier (1.5x, D7's SAME overgrown multiplier) rather than a private
    // duplicate, so the two can never silently drift apart.
    private const int SpawnTileSlack = 4;

    public static int SpawnTileCountFor(int maxLive) =>
        (int)Math.Ceiling(maxLive * EcologyState.OvergrowthCapMultiplier) + SpawnTileSlack;

    // D2 density curve, tuned for "away from road" wilderness geography (monsters, not decor — a stronger
    // road-suppression / longer falloff than any single decor class, since a region's spawn tiles should read
    // as clearly OFF the beaten path, not just thinned near it).
    private const double BaseDensity = 0.9d;
    private const double RoadSuppression = 0.05d;
    private const double RoadFalloffTiles = 12.0d;
    private const double NoiseCellScale = 8.0d;

    // Salts. REGION-SPAWN SEED SALT is arbitrary + distinct from Zone.ResourceNodeSeedSalt (0x5C4A11ED) and the
    // client DecorClass salts (0x1000-0x1005) so this system never shares a draw sequence with either. NOISE
    // SALT further separates the per-tile noise sample from the WeightedScatter draw sequence itself (mirrors
    // DecorPlacer's NoiseSalt).
    private const int RegionSpawnSeedSalt = unchecked((int)0x0EC01096);
    private const int NoiseSalt = 0x2101;

    /// <summary>
    /// Computes the ONE shared road-distance field for the whole zone (road/cobble tiles as BFS seeds), reused
    /// across every region×type derivation. Null <paramref name="authoredMap"/> (a procedural genVersion-1
    /// zone has no SurfaceCategory data) means no roads exist at all — TileDistanceField.Compute with zero
    /// seeds correctly reads as "full wilderness density everywhere" (see its own doc comment), which is exactly
    /// right: a procedural sandbox has no civilization to suppress against.
    /// </summary>
    public static TileDistanceField ComputeRoadDistanceField(AuthoredMap? authoredMap, int width, int height)
    {
        return TileDistanceField.Compute(width, height, CollectRoadSeeds(authoredMap));
    }

    private static List<TileCoord> CollectRoadSeeds(AuthoredMap? authoredMap)
    {
        var seeds = new List<TileCoord>();
        if (authoredMap is null)
        {
            return seeds;
        }

        for (var y = 0; y < authoredMap.Height; y++)
        {
            for (var x = 0; x < authoredMap.Width; x++)
            {
                var category = authoredMap.CategoryAt(x, y);
                if (category is SurfaceCategory.Dirt or SurfaceCategory.Cobble)
                {
                    seeds.Add(new TileCoord(x, y));
                }
            }
        }

        return seeds;
    }

    /// <summary>
    /// Derives the deterministic spawnTiles for ONE region×type: candidates are walkable + (on an authored map)
    /// Grass + inside the region rect (clamped to the zone bounds — a region authored partly or wholly outside
    /// a smaller test zone degrades to fewer/zero tiles rather than throwing, matching WeightedScatter's own
    /// "degrade gracefully" philosophy); density is the away-from-road curve × patch noise (D2); sampling is the
    /// shared WeightedScatter rejection sampler (D3), salted per region×type off the zone seed so every
    /// region×type gets an independent, reproducible draw sequence.
    /// </summary>
    /// <param name="isWalkable">The zone's walkability predicate (works for BOTH authored and procedural maps — unlike AuthoredMap.IsWalkable, which only exists when authoredMap is non-null).</param>
    /// <param name="authoredMap">Null on a procedural (genVersion 1) zone — the category filter is then skipped entirely (every walkable tile counts, matching PlanResourceNodeScatter's own authoredMap-null handling).</param>
    public static IReadOnlyList<TileCoord> DeriveSpawnTiles(
        Func<TileCoord, bool> isWalkable,
        AuthoredMap? authoredMap,
        int zoneWidth,
        int zoneHeight,
        TileDistanceField roadDistanceField,
        int zoneSeed,
        EcologyRegion region,
        string typeId,
        int targetCount,
        int minSpacing)
    {
        ArgumentNullException.ThrowIfNull(isWalkable);
        ArgumentNullException.ThrowIfNull(roadDistanceField);
        ArgumentNullException.ThrowIfNull(region);
        ArgumentNullException.ThrowIfNull(typeId);

        // Clamp the authored rect to the zone bounds. A region authored for the 384x384 town map but evaluated
        // against a smaller test zone (or a future smaller floor) can end up PARTIALLY or ENTIRELY out of
        // bounds; rather than throw, this shrinks to the overlap (possibly empty -> zero spawn tiles, a
        // perfectly legal "this region doesn't exist on this map" outcome).
        var minX = Math.Max(region.MinX, 0);
        var minY = Math.Max(region.MinY, 0);
        var maxX = Math.Min(region.MaxX, zoneWidth - 1);
        var maxY = Math.Min(region.MaxY, zoneHeight - 1);
        if (maxX < minX || maxY < minY)
        {
            return [];
        }

        var width = maxX - minX + 1;
        var height = maxY - minY + 1;

        // FNV-1a over the region/type ids -- NOT string.GetHashCode(), which is RANDOMIZED per process in .NET
        // and would break the "same seed -> identical layout" determinism contract across server restarts.
        var salt = StableHash(region.Id + ":" + typeId);
        var scatterSeed = zoneSeed ^ RegionSpawnSeedSalt ^ salt;

        var tiles = WeightedScatter.Scatter(
            width,
            height,
            scatterSeed,
            local =>
            {
                var abs = new TileCoord(minX + local.X, minY + local.Y);
                if (!isWalkable(abs))
                {
                    return false;
                }

                // AUTHORED-MAP parity with PlanResourceNodeScatter (Zone.cs): only ever land on GRASS when the
                // map has categories at all; a procedural map has none, so every walkable tile counts.
                return authoredMap is null || authoredMap.CategoryAt(abs) == SurfaceCategory.Grass;
            },
            local =>
            {
                var abs = new TileCoord(minX + local.X, minY + local.Y);
                var distance = roadDistanceField.DistanceAt(abs);
                var t = distance >= RoadFalloffTiles ? 1.0 : distance / RoadFalloffTiles;
                var curve = RoadSuppression + ((1.0 - RoadSuppression) * t);
                var noise = ValueNoise.Sample(scatterSeed ^ NoiseSalt, abs.X, abs.Y, NoiseCellScale);
                return Math.Clamp(BaseDensity * curve * noise, 0.0, 1.0);
            },
            targetCount,
            minSpacing);

        if (tiles.Count == 0)
        {
            return [];
        }

        var absolute = new TileCoord[tiles.Count];
        for (var i = 0; i < tiles.Count; i++)
        {
            absolute[i] = new TileCoord(minX + tiles[i].X, minY + tiles[i].Y);
        }

        return absolute;
    }

    // Deterministic 32-bit FNV-1a over a string's UTF-16 code units. Stable across processes/restarts (unlike
    // string.GetHashCode(), which .NET randomizes per process by design) -- required for the "same seed ->
    // identical spawn geography" contract every other procedural system in this codebase relies on.
    private static int StableHash(string value)
    {
        unchecked
        {
            var hash = 2166136261u;
            foreach (var ch in value)
            {
                hash ^= ch;
                hash *= 16777619u;
            }

            return (int)hash;
        }
    }
}
