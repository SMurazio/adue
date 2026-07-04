using Mmo.Shared.Domain;
using Mmo.Shared.Domain.Population;

namespace Mmo.Client.Core.Population;

// PROCEDURAL-POPULATION P2 (docs/procedural-population-design.md D1 L1, D2, D3): the headless, Godot-free
// half of the client decor layer — derives a deterministic instance list per DecorClass from ONLY the
// authored map + zone seed, using the shared P1 math (TileDistanceField, ValueNoise, WeightedScatter). No
// Godot types, no rendering, no server round-trip: two clients that regenerate the same ZoneModel (same
// seed/genVersion, the existing determinism contract) compute byte-identical decor with zero coordination.
//
// GATE (D1 "genVersion 1 zones: NO decor"): this class only accepts an AuthoredMap, which is null on
// non-authored (genVersion 1) zones — ZoneModel.Authored is the caller's natural gate, so there is no
// separate "is this zone allowed to have decor" check here.
public static class DecorPlacer
{
    // One placed decor instance: world (X, Z) INCLUDING the sub-tile jitter offset (so it does not sit
    // dead-center on the tile grid — a purely cosmetic anti-uniformity pass, see JitterInstance), a Y-axis
    // rotation in radians, and a uniform scale multiplier. No SurfaceCategory/tile reference is carried —
    // this is the final render-ready payload; DecorPainter buckets instances into chunks by rounding X/Z
    // back to the nearest tile (safe because MaxSubTileOffset keeps the jitter well under half a tile).
    public readonly record struct DecorInstance(float X, float Z, float RotationRadians, float Scale);

    // Sub-tile position jitter, in tiles. Half-range 0.15 (±MaxSubTileOffset/2) keeps every instance
    // unambiguously closer to its origin tile than to any neighbour, so rounding X/Z recovers the exact
    // origin tile (DecorPainter's chunk bucketing, and the category-filter tests, both rely on this).
    private const float MaxSubTileOffset = 0.30f;

    // Salts folded into (zoneSeed ^ class.Salt) to derive four INDEPENDENT per-tile jitter streams (noise,
    // rotation, scale, offsetX, offsetZ) from the same ValueNoise engine without them all reading the same
    // lattice value. Arbitrary, pairwise-distinct — same discipline as DecorClassTable's per-class salts.
    private const int NoiseSalt = 0x2001;
    private const int RotationSalt = 0x3001;
    private const int ScaleSalt = 0x3002;
    private const int OffsetXSalt = 0x3003;
    private const int OffsetZSalt = 0x3004;

    /// <summary>
    /// Places every class in <see cref="DecorClassTable.Classes"/> against <paramref name="map"/>, keyed
    /// by <see cref="DecorClass.Id"/>. Deterministic: same (map, zoneSeed) always yields the same result,
    /// on every client, every time.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<DecorInstance>> PlaceAll(AuthoredMap map, int zoneSeed)
    {
        ArgumentNullException.ThrowIfNull(map);

        // D2 distanceCurve: ONE BFS distance-to-road field per zone, shared by every class (P1's
        // TileDistanceField, cheap at 147k tiles — see its own doc comment). "Road" = Dirt (`,`) or Cobble
        // (`:`) tiles, matching the design doc's "road/cobble tiles" wording verbatim.
        var field = TileDistanceField.Compute(map.Width, map.Height, CollectRoadSeeds(map));

        var result = new Dictionary<string, IReadOnlyList<DecorInstance>>(DecorClassTable.Classes.Count);
        foreach (var decorClass in DecorClassTable.Classes)
        {
            result[decorClass.Id] = PlaceClass(map, field, zoneSeed, decorClass);
        }

        return result;
    }

    private static List<TileCoord> CollectRoadSeeds(AuthoredMap map)
    {
        var seeds = new List<TileCoord>();
        for (var y = 0; y < map.Height; y++)
        {
            for (var x = 0; x < map.Width; x++)
            {
                var category = map.CategoryAt(x, y);
                if (category is SurfaceCategory.Dirt or SurfaceCategory.Cobble)
                {
                    seeds.Add(new TileCoord(x, y));
                }
            }
        }

        return seeds;
    }

    private static IReadOnlyList<DecorInstance> PlaceClass(
        AuthoredMap map, TileDistanceField field, int zoneSeed, DecorClass decorClass)
    {
        // D3: the shared WeightedScatter engine, salted per class off the zone seed (same discipline as
        // Zone.PlanResourceNodeScatter's own salt) so the five classes never share a draw sequence.
        var scatterSeed = zoneSeed ^ decorClass.Salt;
        var tiles = WeightedScatter.Scatter(
            map.Width,
            map.Height,
            scatterSeed,
            tile => map.IsWalkable(tile) && map.CategoryAt(tile) == decorClass.Category,
            tile => Density(field, zoneSeed, decorClass, tile),
            decorClass.TargetCount,
            decorClass.MinSpacing);

        var instances = new List<DecorInstance>(tiles.Count);
        foreach (var tile in tiles)
        {
            instances.Add(JitterInstance(zoneSeed, decorClass, tile));
        }

        return instances;
    }

    // D2: density(tile) = base(category) x distanceCurve(distanceToRoad) x patchNoise(seed, tile).
    // distanceCurve ramps linearly from RoadSuppression (at the road, d=0) to 1.0 (at d >= RoadFalloffTiles
    // and beyond — including the "no roads at all" int.MaxValue case, which correctly reads as "full
    // wilderness density everywhere", see TileDistanceField's own doc comment on the empty-seeds case).
    private static double Density(TileDistanceField field, int zoneSeed, DecorClass decorClass, TileCoord tile)
    {
        var distance = field.DistanceAt(tile);
        var t = distance >= decorClass.RoadFalloffTiles ? 1.0 : distance / decorClass.RoadFalloffTiles;
        var curve = decorClass.RoadSuppression + ((1.0 - decorClass.RoadSuppression) * t);

        var noise = ValueNoise.Sample(zoneSeed ^ decorClass.Salt ^ NoiseSalt, tile.X, tile.Y, decorClass.NoiseCellScale);

        return Math.Clamp(decorClass.BaseDensity * curve * noise, 0.0, 1.0);
    }

    // Per-instance size/rotation/sub-tile-offset jitter "for life" (P2 task). Reuses ValueNoise as a plain
    // per-tile hash: ValueNoise.Sample(seed, x, y, cellScale: 1.0) samples EXACTLY the lattice corner value
    // at integer tile (x, y) with zero interpolation (see ValueNoise.Sample — at cellScale 1, the fractional
    // cell position is always (0, 0)), i.e. a deterministic uniform [0,1) hash of (seed, tile). Four
    // differently-salted samples give four independent-looking jitter axes without hand-rolling a second
    // PRNG in this assembly (SplitMix64 is internal to Mmo.Shared.Domain.Population by design).
    private static DecorInstance JitterInstance(int zoneSeed, DecorClass decorClass, TileCoord tile)
    {
        var hashSeed = zoneSeed ^ decorClass.Salt;
        var rotationUnit = ValueNoise.Sample(hashSeed ^ RotationSalt, tile.X, tile.Y, 1.0);
        var scaleUnit = ValueNoise.Sample(hashSeed ^ ScaleSalt, tile.X, tile.Y, 1.0);
        var offsetXUnit = ValueNoise.Sample(hashSeed ^ OffsetXSalt, tile.X, tile.Y, 1.0);
        var offsetZUnit = ValueNoise.Sample(hashSeed ^ OffsetZSalt, tile.X, tile.Y, 1.0);

        var rotation = (float)(rotationUnit * 2.0 * Math.PI);
        var scale = (float)(1.0 + (decorClass.ScaleJitter * ((scaleUnit * 2.0) - 1.0)));
        var offsetX = (float)((offsetXUnit - 0.5) * MaxSubTileOffset);
        var offsetZ = (float)((offsetZUnit - 0.5) * MaxSubTileOffset);

        return new DecorInstance(tile.X + offsetX, tile.Y + offsetZ, rotation, scale);
    }
}
