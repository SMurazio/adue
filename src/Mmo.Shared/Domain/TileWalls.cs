using System.Collections.Generic;

namespace Mmo.Shared.Domain;

// CONTINUOUS MIGRATION (Phase 2): the SHARED, DETERMINISTIC tile -> collision-wall derivation. A blocked tile is a
// solid 1x1 box; the player's swept-circle resolver (ContinuousCollision) collides against these boxes. This lives in
// Mmo.Shared.Domain (alongside the resolver) because the Phase-4 client predictor must derive the EXACT SAME walls
// from the EXACT SAME blocked-tile set via the EXACT SAME function — that is the determinism linchpin (the client
// regenerates an identical HashSet<TileCoord> from (Width,Height,Seed,GenVersion) via TerrainGenerator, so it can
// re-derive the same walls with no new wire payload for the map).
//
// TILE GEOMETRY: a tile centre is the integer pair (tx, ty); a blocked tile (tx, ty) occupies the AABB
// [tx-0.5, ty-0.5 .. tx+0.5, ty+0.5] (1x1, tile pitch 1). The body radius (0.5) inscribes a 1x1 body.
//
// ORDER STABILITY: NeighborhoodWalls emits walls in STABLE ROW-MAJOR order (y outer, x inner) — never in HashSet
// iteration order — so the Wall[] handed to the resolver is byte-identical on both sides for the same blocked set +
// box. Do NOT iterate `blocked` directly; iterate the box and probe `blocked.Contains` so the order is positional.
public static class TileWalls
{
    // The collision wall (1x1 solid box) for a blocked tile, centred on the tile and half-extent 0.5 each axis.
    // ForTile((tx,ty)) -> Wall.FromCenter(tx, ty, 0.5, 0.5) -> AABB [tx-0.5,ty-0.5 .. tx+0.5,ty+0.5]. PURE.
    public static ContinuousCollision.Wall ForTile(TileCoord tile) =>
        ContinuousCollision.Wall.FromCenter(tile.X, tile.Y, 0.5d, 0.5d);

    // Derive the walls for every blocked tile inside an INCLUSIVE tile box [minTileX..maxTileX] x [minTileY..maxTileY],
    // appended to `output` in STABLE ROW-MAJOR order (y outer, x inner). The caller computes the box from the swept
    // body AABB (start+end, expanded by radius, floored/ceiled) — see the server's QueryNearbyWalls. The list is a
    // deterministic SUPERSET of the swept+radius region's blocked tiles (the box is conservative). `output` is the
    // caller's reused scratch buffer; this CLEARS it first (zero per-tick alloc at the call site). PURE w.r.t. the
    // inputs — same `blocked` + same box => same appended Wall[] in the same order.
    public static void NeighborhoodWalls(
        IReadOnlySet<TileCoord> blocked,
        int minTileX,
        int minTileY,
        int maxTileX,
        int maxTileY,
        List<ContinuousCollision.Wall> output)
    {
        output.Clear();
        if (blocked.Count == 0)
        {
            return;
        }

        // Row-major: y outer, x inner. Positional probe (blocked.Contains) — NOT iteration of `blocked` — so the
        // emitted order is stable regardless of the set's internal layout (the byte-identity guarantee).
        for (var ty = minTileY; ty <= maxTileY; ty++)
        {
            for (var tx = minTileX; tx <= maxTileX; tx++)
            {
                var tile = new TileCoord(tx, ty);
                if (blocked.Contains(tile))
                {
                    output.Add(ForTile(tile));
                }
            }
        }
    }
}
