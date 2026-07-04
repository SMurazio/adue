using Mmo.Shared.Domain;
using Mmo.Shared.Domain.Population;

namespace Mmo.Client.Core.Population;

// NODE-FIELD N3 (docs/node-field-design.md D6): chunks the ~5,000-entry catalogue by TILE onto the SAME
// 32-tile grid the floor/walls/decor already partition into (Mmo.Client.Godot.Visuals.TerrainPainter
// .ChunkTiles). This assembly is Godot-free, so the constant is DUPLICATED here rather than shared across
// assemblies — the same duplication-over-shared-helper tradeoff NodeCatalog.Density already accepts (its own
// comment: "there is no common assembly to hang a shared helper off without a bigger refactor"). Keep
// ChunkTiles in sync with TerrainPainter.ChunkTiles if either ever changes.
//
// Two consumers share ONE build:
//   * NodeFieldPainter (Godot) groups each chunk's entries into one MultiMesh per (NodeType, availability)
//     so a depleted flip rebuilds ONLY that chunk's buffer (D6 "microseconds"), instead of every chunk.
//   * NodeFieldTargeting scans only the actor's chunk plus its 8 neighbours instead of a linear scan over the
//     whole catalogue — the interaction reach (InteractionTuning.InteractionRadiusUnits, 1.5 tiles) is far
//     smaller than one 32-tile chunk, so the 3x3 neighbourhood always contains every in-reach candidate, even
//     right at a chunk boundary.
//
// N1+N2 FABLE REVIEW FINDING: the client's mirrored depleted-index set (MmoClient._depletedNodeIndices) is
// NOT bounds-checked against the catalogue it was built from — a drifted/hostile server value must not throw
// when a consumer resolves it back to a chunk. TryChunkOfIndex/ChunksTouchedBy are the ONE place that
// resolves a wire index back into this index's data, and both are bounds-safe: an out-of-range index is
// silently ignored (returns false / omitted from the result), never indexed directly.
public sealed class NodeFieldChunkIndex
{
    public const int ChunkTiles = 32;

    private readonly Dictionary<(int Cx, int Cz), List<NodeCatalogEntry>> _byChunk;

    // index -> chunk key, sized to the catalogue's entry count. Entry.Index is always the entry's own
    // position in NodeCatalog.Entries (NodeCatalog.Build appends in index order), so this array is a safe,
    // O(1) reverse lookup once bounds-checked against its Length — no need to re-touch the catalogue itself.
    private readonly (int Cx, int Cz)[] _chunkByIndex;

    private NodeFieldChunkIndex(Dictionary<(int Cx, int Cz), List<NodeCatalogEntry>> byChunk, (int Cx, int Cz)[] chunkByIndex)
    {
        _byChunk = byChunk;
        _chunkByIndex = chunkByIndex;
    }

    public static NodeFieldChunkIndex Build(NodeCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        var byChunk = new Dictionary<(int Cx, int Cz), List<NodeCatalogEntry>>();
        var chunkByIndex = new (int Cx, int Cz)[catalog.Entries.Count];
        foreach (var entry in catalog.Entries)
        {
            var key = ChunkOf(entry.Tile);
            if (!byChunk.TryGetValue(key, out var list))
            {
                list = new List<NodeCatalogEntry>();
                byChunk[key] = list;
            }

            list.Add(entry);
            chunkByIndex[entry.Index] = key;
        }

        return new NodeFieldChunkIndex(byChunk, chunkByIndex);
    }

    public static (int Cx, int Cz) ChunkOf(TileCoord tile) => (tile.X / ChunkTiles, tile.Y / ChunkTiles);

    public IReadOnlyCollection<(int Cx, int Cz)> ChunkKeys => _byChunk.Keys;

    public IReadOnlyList<NodeCatalogEntry> EntriesIn((int Cx, int Cz) chunk) =>
        _byChunk.TryGetValue(chunk, out var list) ? list : Array.Empty<NodeCatalogEntry>();

    // Bounds-safe reverse lookup (the Fable-review fix): an index the mirror carries that this catalogue
    // never actually issued (drift, or a hostile/corrupted value) returns false instead of throwing.
    public bool TryChunkOfIndex(ushort index, out (int Cx, int Cz) chunk)
    {
        if (index >= _chunkByIndex.Length)
        {
            chunk = default;
            return false;
        }

        chunk = _chunkByIndex[index];
        return true;
    }

    // Every DISTINCT chunk key touched by `indices` — out-of-range indices are silently skipped
    // (TryChunkOfIndex). Used to resolve "which chunk(s) does this NodeState flip / login batch affect" so
    // the renderer rebuilds only those, never the whole field.
    public HashSet<(int Cx, int Cz)> ChunksTouchedBy(IEnumerable<ushort> indices)
    {
        ArgumentNullException.ThrowIfNull(indices);

        var touched = new HashSet<(int Cx, int Cz)>();
        foreach (var index in indices)
        {
            if (TryChunkOfIndex(index, out var chunk))
            {
                touched.Add(chunk);
            }
        }

        return touched;
    }
}
