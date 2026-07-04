using Mmo.Shared.Domain;

namespace Mmo.Client.Core.Population;

// PROCEDURAL-POPULATION P2 (docs/procedural-population-design.md D1 L1, D4): the client-only decor class
// table. D4 says L1 (client) classes need no json — "a mirrored in-code table for L1 client classes" —
// so this is the whole authoring surface for grass tufts / flowers / pebbles / etc: pure presentation,
// zero entities, zero wire, zero server involvement (the server never parses or knows this file exists).
//
// Two decor SHAPES (graybox aesthetic, D1 "tens of thousands of instances"):
//   Cross     — two crossed vertical quads (Minecraft-style grass billboard), pivot at the BOTTOM so the
//               tuft/flower "grows" out of the ground. Width = horizontal span of each arm, Height =
//               vertical extent. Built once per class as a shared ArrayMesh (DecorPainter.BuildCrossMesh).
//   FlatQuad  — one flat plane lying on the ground (pebbles, dry patches), same PlaneMesh convention the
//               authored floor uses. Width/Height here are the plane's (X, Z) footprint, not a height.
public enum DecorShape : byte
{
    Cross,
    FlatQuad,
}

// One row of the class table. Placement math (DecorPlacer) reads Category/MinSpacing/BaseDensity/
// RoadSuppression/RoadFalloffTiles/NoiseCellScale/TargetCount/Salt; rendering (DecorPainter) reads
// Shape/Width/Height/Color/ScaleJitter. Kept as one record instead of two split types because every class
// needs both halves and there is no reuse pressure to split them.
public sealed record DecorClass(
    string Id,
    SurfaceCategory Category,
    int MinSpacing,
    int TargetCount,
    double BaseDensity,
    double RoadSuppression,
    double RoadFalloffTiles,
    double NoiseCellScale,
    int Salt,
    DecorShape Shape,
    float Width,
    float Height,
    float ScaleJitter,
    (float R, float G, float B) Color);

// PROCEDURAL-POPULATION P2 §3 perf posture: "target <=30k instances at 4-6 verts each". The five classes
// below sum their TargetCount to 27,000 — under budget with headroom, and DecorClassTableTests pins the
// sum so a future class addition that blows the budget fails loudly instead of silently regressing frame
// time. TargetCount is a FIXED cap (not scaled by map area) so the budget holds regardless of how big a
// future floor's authored map is (P2 task note) — a smaller map just runs out of WeightedScatter attempt
// budget sooner and places fewer, which is fine (WeightedScatter already degrades gracefully, P1).
//
// Salts are arbitrary, pairwise-distinct constants (same discipline as Zone.ResourceNodeSeedSalt) so the
// five classes never share a WeightedScatter draw sequence even when derived from the same zone seed.
// Colors sit near the AuthoredSurfaceVisuals palette family (grass green / dirt brown) but are NOT
// identical to the floor tint, so decor reads as a distinct layer on top of the floor instead of
// disappearing into it.
public static class DecorClassTable
{
    public static readonly IReadOnlyList<DecorClass> Classes =
    [
        // Small grass tufts: the bulk of the "alive meadow" read. Dense, cheap, tight spacing.
        new DecorClass(
            Id: "grass_tuft_small",
            Category: SurfaceCategory.Grass,
            MinSpacing: 1,
            TargetCount: 12_000,
            BaseDensity: 0.85,
            RoadSuppression: 0.10,
            RoadFalloffTiles: 10,
            NoiseCellScale: 6,
            Salt: 0x1001,
            Shape: DecorShape.Cross,
            Width: 0.34f,
            Height: 0.26f,
            ScaleJitter: 0.30f,
            Color: (0.30f, 0.56f, 0.20f)),

        // Taller grass tufts: sparser, bigger, darker — reads as thicket texture layered over the small tufts.
        new DecorClass(
            Id: "grass_tuft_tall",
            Category: SurfaceCategory.Grass,
            MinSpacing: 2,
            TargetCount: 6_000,
            BaseDensity: 0.50,
            RoadSuppression: 0.05,
            RoadFalloffTiles: 14,
            NoiseCellScale: 10,
            Salt: 0x1002,
            Shape: DecorShape.Cross,
            Width: 0.40f,
            Height: 0.50f,
            ScaleJitter: 0.25f,
            Color: (0.20f, 0.40f, 0.15f)),

        // Flower accents: rare, wide spacing, bright color pop against the green.
        new DecorClass(
            Id: "flower_accent",
            Category: SurfaceCategory.Grass,
            MinSpacing: 3,
            TargetCount: 3_000,
            BaseDensity: 0.18,
            RoadSuppression: 0.05,
            RoadFalloffTiles: 8,
            NoiseCellScale: 5,
            Salt: 0x1003,
            Shape: DecorShape.Cross,
            Width: 0.20f,
            Height: 0.22f,
            ScaleJitter: 0.35f,
            Color: (0.85f, 0.74f, 0.15f)),

        // Pebbles on dirt/road tiles. Less road-suppressed than the grass classes (a pebble on a dirt path
        // reads fine — it's the grass classes civilization is supposed to clear, not loose gravel).
        new DecorClass(
            Id: "pebble",
            Category: SurfaceCategory.Dirt,
            MinSpacing: 2,
            TargetCount: 4_000,
            BaseDensity: 0.40,
            RoadSuppression: 0.30,
            RoadFalloffTiles: 6,
            NoiseCellScale: 6,
            Salt: 0x1004,
            Shape: DecorShape.FlatQuad,
            Width: 0.22f,
            Height: 0.22f,
            ScaleJitter: 0.30f,
            Color: (0.45f, 0.43f, 0.40f)),

        // Dry patches: sparse, large, low-contrast — a texture break on the dirt, not a focal object.
        new DecorClass(
            Id: "dry_patch",
            Category: SurfaceCategory.Dirt,
            MinSpacing: 4,
            TargetCount: 2_000,
            BaseDensity: 0.15,
            RoadSuppression: 0.40,
            RoadFalloffTiles: 6,
            NoiseCellScale: 12,
            Salt: 0x1005,
            Shape: DecorShape.FlatQuad,
            Width: 0.60f,
            Height: 0.60f,
            ScaleJitter: 0.20f,
            Color: (0.58f, 0.46f, 0.30f)),
    ];
}
