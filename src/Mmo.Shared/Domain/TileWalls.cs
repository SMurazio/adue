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

    // CONTINUOUS MIGRATION (Phase 4): the SHARED swept-AABB box-math + wall derivation for ONE per-input move. Given the
    // body's start, this input's delta (velocity × dt) and radius, compute the swept body AABB (start..end, each
    // expanded by radius), floor/ceil it to an INCLUSIVE tile box, and emit one collision Wall per blocked tile inside
    // that box — in STABLE ROW-MAJOR order, into the caller's REUSED scratch buffer (zero per-move alloc). This is the
    // EXACT box-math the server's TileGrid.QueryNearbyWalls used inline (now a forwarder to this); the Phase-4 client
    // predictor calls THIS so server and prediction derive a byte-identical wall set from the same (blocked, start,
    // delta, radius) — the determinism linchpin. PURE w.r.t. the inputs.
    //
    // The box is a deterministic SUPERSET of the swept+radius region (over-covers by at most one tile each side via
    // floor/ceil); the resolver harmlessly ignores walls the circle never reaches. NOT clamped to grid bounds — an
    // out-of-range probe is a cheap Contains miss; the perimeter ring is always blocked anyway.
    public static void NeighborhoodWallsForMove(
        IReadOnlySet<TileCoord> blocked,
        WorldVector start,
        WorldVector delta,
        double radius,
        List<ContinuousCollision.Wall> output)
    {
        var endX = start.X + delta.X;
        var endY = start.Y + delta.Y;

        // Swept AABB of the body centre over the move, expanded by the radius on every side.
        var minX = System.Math.Min(start.X, endX) - radius;
        var maxX = System.Math.Max(start.X, endX) + radius;
        var minY = System.Math.Min(start.Y, endY) - radius;
        var maxY = System.Math.Max(start.Y, endY) + radius;

        // Floor/ceil to the inclusive tile box (a conservative superset — see the method comment).
        var minTileX = (int)System.Math.Floor(minX);
        var maxTileX = (int)System.Math.Ceiling(maxX);
        var minTileY = (int)System.Math.Floor(minY);
        var maxTileY = (int)System.Math.Ceiling(maxY);

        NeighborhoodWalls(blocked, minTileX, minTileY, maxTileX, maxTileY, output);
    }

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
