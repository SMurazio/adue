namespace Mmo.Shared.Domain.Population;

// NODE-FIELD N1 (docs/node-field-design.md D1/D8): the harvestable node type identity. Byte-backed
// because it is hashed into NodeCatalog's CatalogHash (D2) -- never reorder/renumber existing values,
// only append, or every shipped catalogue's hash silently moves (same discipline as
// AuthoredMarkerKind/SurfaceCategory). Values mirror the three existing resource-node type ids
// (ResourceNodeRegistry.CreateDefault: "tree" -> wood, "rock" -> stone, "plant" -> fiber) -- N1 does not
// invent new node kinds, it only relocates where instances of the existing three kinds live.
public enum NodeType : byte
{
    Tree = 0,
    Rock = 1,
    Plant = 2,
}

// One row of the shared node class table -- the code-authored placement recipe for one NodeType,
// deliberately shaped like Mmo.Client.Core.Population.DecorClass (same P1 consumers: category filter,
// MinSpacing, TargetCount, the D2 density composition params, a per-class scatter salt) MINUS the
// client-rendering fields (Shape/Width/Height/Color/ScaleJitter) -- N1 is shared placement data only;
// N3 owns how each NodeType is drawn. Category is a field (not hardcoded) for the same reason DecorClass
// keeps it: today all three node classes are Grass-only (D8), but a future class (e.g. a reed patch on
// Dirt) should be a new table row, not a code change.
public sealed record NodeClass(
    NodeType Type,
    SurfaceCategory Category,
    int TargetCount,
    int MinSpacing,
    double BaseDensity,
    double RoadSuppression,
    double RoadFalloffTiles,
    double NoiseCellScale,
    int Salt);

// D8 content targets, DENSIFIED on user feel-test feedback (2026-07-04, "I would like even more
// forest"): total ~8,300 nodes on the 384x384 map -- Tree ~6,500 (spacing dropped 3->2 so thickets
// read as WOODS rather than orchards), Rock ~1,000, Plant ~800 (undergrowth). Road/town suppression is
// strong (low RoadSuppression floor, generous RoadFalloffTiles) so the town/road grass strip stays
// sparse while the wings and the Verge read as genuine forest; NoiseCellScale is wide (10-16 tiles per
// patch, vs decor's 5-12) so trees in particular clump into THICKETS rather than an even sprinkle (D8
// "thick clusters via patch noise"). Salts are arbitrary, pairwise-distinct, and deliberately a
// different constant family than DecorClassTable's 0x1000s / Zone.ResourceNodeSeedSalt (0x5C4A11ED) so
// a node class scatter can never alias another population system's PRNG stream even when folded
// against the same zone seed.
public static class NodeClassTable
{
    public static readonly IReadOnlyList<NodeClass> Classes =
    [
        new NodeClass(
            Type: NodeType.Tree,
            Category: SurfaceCategory.Grass,
            TargetCount: 6_500,
            MinSpacing: 2,
            BaseDensity: 0.62,
            RoadSuppression: 0.08,
            RoadFalloffTiles: 14,
            NoiseCellScale: 16,
            Salt: 0x9001),

        new NodeClass(
            Type: NodeType.Rock,
            Category: SurfaceCategory.Grass,
            TargetCount: 1_000,
            MinSpacing: 4,
            BaseDensity: 0.35,
            RoadSuppression: 0.12,
            RoadFalloffTiles: 10,
            NoiseCellScale: 10,
            Salt: 0x9002),

        new NodeClass(
            Type: NodeType.Plant,
            Category: SurfaceCategory.Grass,
            TargetCount: 800,
            MinSpacing: 3,
            BaseDensity: 0.30,
            RoadSuppression: 0.15,
            RoadFalloffTiles: 8,
            NoiseCellScale: 8,
            Salt: 0x9003),
    ];
}
