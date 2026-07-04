using Mmo.Shared.Domain;
using Mmo.Shared.Domain.Population;

namespace Mmo.Client.Core.Population;

// NODE-FIELD N3 (docs/node-field-design.md D6): the headless, Godot-free half of the client field layer.
// The catalogue already fixed every node's TILE (NodeCatalog.Build); this only adds the SAME "for life"
// per-instance jitter (sub-tile offset, Y rotation, scale) DecorPlacer.JitterInstance gives scattered decor —
// reused verbatim shape (DecorPlacer.DecorInstance) so NodeFieldPainter can pack the result through the
// EXISTING MultiMeshTileBuffer.PackDecorTransforms with no new buffer layout. Distinct salts from both
// DecorPlacer's (0x2001/0x3001-3004) and NodeCatalog's own noise salt (0xA001) so the three jitter/placement
// streams folded against the same zone seed never alias.
//
// Deterministic: identical (catalogue, zoneSeed) always yields the same placement list, on every client,
// every time — the catalogue itself is already the shared/deterministic part (N1); this is a pure
// presentation-only derivation from it.
public static class NodeFieldPlacer
{
    // One node ready to render: the catalogue Index (the wire identity — keys the depleted-set lookup and
    // HarvestNodeMessage), its NodeType (mesh/material group), and the jittered instance transform in the
    // SAME shape DecorPlacer produces. PlaceAll emits exactly one PlacedNode per catalogue entry, IN INDEX
    // ORDER, so `placements[entry.Index] == ` the entry's own placement — callers may index this list
    // directly by a catalogue Index already known to be in range (e.g. via NodeFieldChunkIndex, which only
    // ever yields entries it built from the SAME catalogue).
    public readonly record struct PlacedNode(int Index, NodeType Type, DecorPlacer.DecorInstance Instance);

    // Mirrors DecorPlacer's own bound exactly (same reasoning: keeps an instance unambiguously closer to its
    // origin tile than to any neighbour).
    private const float MaxSubTileOffset = 0.30f;

    // Salts folded into (zoneSeed ^ NodeTypeSalt ^ entry.Index) to derive three INDEPENDENT per-node jitter
    // streams (rotation, scale, sub-tile offset) — arbitrary, pairwise-distinct, a different constant family
    // than DecorPlacer's / NodeCatalog's own salts (see the type comment).
    private const int RotationSalt = 0x7001;
    private const int ScaleSalt = 0x7002;
    private const int OffsetXSalt = 0x7003;
    private const int OffsetZSalt = 0x7004;

    // Per-NodeType scale-jitter half-range. NodeClass (Mmo.Shared) deliberately carries no render fields
    // ("N3 owns how each NodeType is drawn" — its own type comment), so these live here rather than on the
    // shared table.
    private const double TreeScaleJitter = 0.15d;
    private const double RockScaleJitter = 0.20d;
    private const double PlantScaleJitter = 0.25d;

    public static IReadOnlyList<PlacedNode> PlaceAll(NodeCatalog catalog, int zoneSeed)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        var result = new List<PlacedNode>(catalog.Entries.Count);
        foreach (var entry in catalog.Entries)
        {
            result.Add(new PlacedNode(entry.Index, entry.NodeType, JitterInstance(zoneSeed, entry)));
        }

        return result;
    }

    // Per-instance size/rotation/sub-tile-offset jitter "for life" — same ValueNoise-as-plain-per-tile-hash
    // trick DecorPlacer.JitterInstance uses (ValueNoise.Sample(seed, x, y, cellScale: 1.0) samples exactly the
    // lattice corner at integer (x, y), i.e. a deterministic uniform [0,1) hash of (seed, tile)). The entry's
    // OWN Index is folded into the hash seed (not just its tile) so two same-type nodes could in principle
    // share a tile-adjacent hash lattice cell without reading identical jitter — decor only ever keys off the
    // tile because a tile carries at most one instance per class; a node's Index is already unique identity,
    // so folding it in costs nothing and only adds variety.
    private static DecorPlacer.DecorInstance JitterInstance(int zoneSeed, NodeCatalogEntry entry)
    {
        var hashSeed = zoneSeed ^ NodeTypeSalt(entry.NodeType) ^ entry.Index;
        var tile = entry.Tile;
        var rotationUnit = ValueNoise.Sample(hashSeed ^ RotationSalt, tile.X, tile.Y, 1.0);
        var scaleUnit = ValueNoise.Sample(hashSeed ^ ScaleSalt, tile.X, tile.Y, 1.0);
        var offsetXUnit = ValueNoise.Sample(hashSeed ^ OffsetXSalt, tile.X, tile.Y, 1.0);
        var offsetZUnit = ValueNoise.Sample(hashSeed ^ OffsetZSalt, tile.X, tile.Y, 1.0);

        var rotation = (float)(rotationUnit * 2.0 * Math.PI);
        var jitterHalfRange = ScaleJitterFor(entry.NodeType);
        var scale = (float)(1.0 + (jitterHalfRange * ((scaleUnit * 2.0) - 1.0)));
        var offsetX = (float)((offsetXUnit - 0.5) * MaxSubTileOffset);
        var offsetZ = (float)((offsetZUnit - 0.5) * MaxSubTileOffset);

        return new DecorPlacer.DecorInstance(tile.X + offsetX, tile.Y + offsetZ, rotation, scale);
    }

    private static int NodeTypeSalt(NodeType type) => type switch
    {
        NodeType.Tree => 0x8101,
        NodeType.Rock => 0x8102,
        NodeType.Plant => 0x8103,
        _ => 0x8104,
    };

    private static double ScaleJitterFor(NodeType type) => type switch
    {
        NodeType.Tree => TreeScaleJitter,
        NodeType.Rock => RockScaleJitter,
        NodeType.Plant => PlantScaleJitter,
        _ => TreeScaleJitter,
    };
}
