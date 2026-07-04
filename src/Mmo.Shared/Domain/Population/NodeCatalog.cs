namespace Mmo.Shared.Domain.Population;

// One catalogue entry (node-field-design D1): Index is the node's PERMANENT id -- the ushort-sized
// handle N2's protocol/harvest messages will reference instead of ever putting a position on the wire.
// Never re-sort or renumber an existing entry's Index once shipped; see NodeCatalog.Build's pin-stability
// contract below for why Index is stable across class-table edits.
public readonly record struct NodeCatalogEntry(int Index, TileCoord Tile, NodeType NodeType);

// NODE-FIELD N1 (docs/node-field-design.md D1/D2): the shared deterministic node catalogue -- computed
// identically by BOTH sides from (zone seed, authored map) so a harvestable node's position never
// crosses the wire (only its Index does, starting at N2). This file produces DATA ONLY: nothing consumes
// NodeCatalog yet (no protocol/server-state/client-rendering changes -- those are N2/N3), so building one
// is a pure, side-effect-free computation exactly like TerrainGenerator.GenerateLayout.
//
// Build order (the pin-stability contract, D1 "authored T/R marker pins FIRST (stable low indices)"):
//   1. Every authored TreePin/RockPin marker, in AuthoredMap.Markers' row-major (y, then x) scan order --
//      these always occupy indices [0, pinCount) no matter how the class table below changes shape,
//      because they are placed before ANY scatter class runs and scatter classes never reorder or
//      displace an already-placed entry.
//   2. Per NodeClass in NodeClassTable.Classes order (Tree, Rock, Plant), a WeightedScatter pass (P1 math,
//      the SAME density composition DecorPlacer/P2 uses: base(category) x distanceCurve(distanceToRoad)
//      x patchNoise(seed, tile)) -- appended after the pins and after every earlier class's placements.
// Determinism: identical (seed, map, classes) always yields a byte-identical entry list and CatalogHash,
// on every platform/runtime, for the same reason WeightedScatter/ValueNoise/TileDistanceField already are
// (pure SplitMix64 arithmetic, no System.Random, no clocks).
public sealed class NodeCatalog
{
    // Salted into (zoneSeed ^ class.Salt) to derive the patch-noise stream, independent of the
    // WeightedScatter draw stream itself -- mirrors DecorPlacer's own NoiseSalt discipline exactly (same
    // reason: two different XORed constants so the placement draws and the density-noise draws are two
    // independent lattices, not the same PRNG stream read twice for different purposes).
    private const int NoiseSalt = 0xA001;

    private NodeCatalog(IReadOnlyList<NodeCatalogEntry> entries, ulong catalogHash)
    {
        Entries = entries;
        CatalogHash = catalogHash;
    }

    /// <summary>The full catalogue, pins first then scatter, in stable index order.</summary>
    public IReadOnlyList<NodeCatalogEntry> Entries { get; }

    /// <summary>
    /// D2: the drift guard that will ride ZoneInfo in N2 and hard-fail a client whose catalogue differs
    /// from the server's. See <see cref="ComputeCatalogHash"/> for the exact canonical chain.
    /// </summary>
    public ulong CatalogHash { get; }

    /// <summary>
    /// Builds the catalogue for <paramref name="map"/> under <paramref name="seed"/>. <paramref
    /// name="classes"/> defaults to the shipped <see cref="NodeClassTable.Classes"/>; tests pass an
    /// alternate table to prove the catalogue (and hence CatalogHash) is a pure function of the class
    /// table, without mutating the shipped one.
    /// </summary>
    public static NodeCatalog Build(int seed, AuthoredMap map, IReadOnlyList<NodeClass>? classes = null)
    {
        ArgumentNullException.ThrowIfNull(map);
        classes ??= NodeClassTable.Classes;

        var entries = new List<NodeCatalogEntry>();

        // Step 1: authored pins, stable low indices (D1). map.Markers is already row-major by
        // AuthoredMap.Parse's construction, so filtering it preserves that canonical order verbatim --
        // no defensive sort needed, same as every other consumer of AuthoredMap's emitted lists.
        foreach (var marker in map.Markers)
        {
            var nodeType = marker.Kind switch
            {
                AuthoredMarkerKind.TreePin => NodeType.Tree,
                AuthoredMarkerKind.RockPin => NodeType.Rock,
                _ => (NodeType?)null,
            };

            if (nodeType is { } type)
            {
                entries.Add(new NodeCatalogEntry(entries.Count, marker.Tile, type));
            }
        }

        // Every authored marker tile (pins AND H/P props) is off-limits to scatter -- a tree/rock/plant
        // must never stack onto a pinned node or under a house/portal anchor. Grows as each class scatters
        // so LATER classes also never land on an EARLIER class's placements (no two catalogue entries can
        // ever share a tile).
        var claimed = new HashSet<TileCoord>(map.Markers.Select(m => m.Tile));

        // Step 2: one BFS distance-to-road field for the whole build, shared by every class (P1 cost:
        // single-digit ms at 147k tiles) -- same "road" definition as DecorPlacer (Dirt or Cobble tiles).
        var field = TileDistanceField.Compute(map.Width, map.Height, CollectRoadSeeds(map));

        foreach (var nodeClass in classes)
        {
            var scatterSeed = seed ^ nodeClass.Salt;
            var tiles = WeightedScatter.Scatter(
                map.Width,
                map.Height,
                scatterSeed,
                tile => map.IsWalkable(tile) && map.CategoryAt(tile) == nodeClass.Category,
                tile => Density(field, seed, nodeClass, tile),
                nodeClass.TargetCount,
                nodeClass.MinSpacing,
                preclaimed: claimed);

            foreach (var tile in tiles)
            {
                entries.Add(new NodeCatalogEntry(entries.Count, tile, nodeClass.Type));
                claimed.Add(tile);
            }
        }

        return new NodeCatalog(entries, ComputeCatalogHash(entries));
    }

    // D2 density(tile) = base(category) x distanceCurve(distanceToRoad) x patchNoise(seed, tile) --
    // IDENTICAL shape to DecorPlacer.Density (duplicated, not shared, the same way SplitMix64 is
    // duplicated across TerrainGenerator/Zone/Population: DecorPlacer lives in the CLIENT-only
    // Mmo.Client.Core assembly and this is shared code, so there is no common assembly to hang a shared
    // helper off without a bigger refactor out of N1's scope).
    private static double Density(TileDistanceField field, int seed, NodeClass nodeClass, TileCoord tile)
    {
        var distance = field.DistanceAt(tile);
        var t = distance >= nodeClass.RoadFalloffTiles ? 1.0 : distance / nodeClass.RoadFalloffTiles;
        var curve = nodeClass.RoadSuppression + ((1.0 - nodeClass.RoadSuppression) * t);

        var noise = ValueNoise.Sample(seed ^ nodeClass.Salt ^ NoiseSalt, tile.X, tile.Y, nodeClass.NoiseCellScale);

        return Math.Clamp(nodeClass.BaseDensity * curve * noise, 0.0, 1.0);
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

    // Stable 64-bit FNV-1a hash over the catalogue in index order -- mirrors
    // TerrainGenerator.ContentHash's exact mixing shape (MixInt32 byte-by-byte, MixByte) but is its own
    // independent chain (TerrainGenerator's mixers are private to that class), the same duplication-over-
    // shared-helper tradeoff as Density above.
    //
    // Canonical order (COMPATIBILITY CONTRACT -- N2 rides this hash on ZoneInfo and hard-fails a drifted
    // client; changing this order/shape invalidates every shipped catalogue's hash):
    //   1. entry count (length-prefixed, so a truncated/extended catalogue can never alias a different one),
    //   2. per entry, in index order: Index, Tile.X, Tile.Y, NodeType byte.
    // Only append a new section AFTER this one if the entry schema ever grows a new hashed field; never
    // reorder or remove an existing mixed value.
    public static ulong ComputeCatalogHash(IReadOnlyList<NodeCatalogEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        const ulong fnvOffset = 14695981039346656037UL;
        const ulong fnvPrime = 1099511628211UL;

        var hash = fnvOffset;
        hash = MixInt32(hash, fnvPrime, entries.Count);
        foreach (var entry in entries)
        {
            hash = MixInt32(hash, fnvPrime, entry.Index);
            hash = MixInt32(hash, fnvPrime, entry.Tile.X);
            hash = MixInt32(hash, fnvPrime, entry.Tile.Y);
            hash = MixByte(hash, fnvPrime, (byte)entry.NodeType);
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
}
