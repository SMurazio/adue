# S36b — Godot per-chunk terrain render + culling

Severity: should-fix for big/dense worlds. **Split from the original S36** (Orchestrator decision).
**Depends on S36a** (chunked terrain on the wire). See `docs/terrain-and-map-design.md`.

## Why

Even after S36a bounds terrain *bandwidth* by AOI, the Godot client still renders walls as **one global
wall MultiMesh with no culling**. With dense interior maps that is a ~hundreds-of-K-instance mesh
(~millions of tris/frame, mostly off-screen). Rendered primitives must be bounded by **view**, not by
map size.

## Scope (THIS task = Godot client rendering only)

Replace the single global wall MultiMesh with **per-chunk rendering** that culls and frees independently.
No protocol/server changes (S36a already streams chunks).

1. **A dictionary of loaded chunks** keyed by `(chunkX, chunkY)`; each chunk owns **its own wall
   MultiMesh / node**, built from that chunk's `tileData` (received via S36a's `TerrainChunk`).
2. **Cull per chunk:** a per-chunk node lets Godot frustum-cull automatically; additionally distance-cull
   chunks beyond the view/AOI (hide or free). Off-view chunks must not contribute draw primitives.
3. **Free chunks** that leave range (and rebuild on re-entry from cached/`re-sent` chunk data). If S36a
   deferred server-side chunk *unload*, client-side hide/free by distance is still required here.
4. Remove the old single global wall MultiMesh path.

## Files
- `src/Mmo.Client.Godot/` — per-chunk terrain node/MultiMesh manager, replacing the global wall mesh;
  frustum/distance culling; build-from-chunk + free-on-leave.
- `src/Mmo.Client.Core/` only if the chunk store needs a shared structure (keep server-agnostic).

## Acceptance
- Client rendered primitives are bounded by **view**, not map size: on a 2048² dense map, off-view chunks
  are culled/freed (verify via the F3 HUD / draw stats and the visual check — walls in view are correct,
  off-view chunks cost ~nothing).
- Moving across the map loads/frees chunks with no missing walls in view and no unbounded growth in
  loaded-chunk count.
- `godot-build.cmd` green; a visual check via the Godot client (use the `mmo-client-control` MCP and/or
  `start-godot-visual-check`); `run-checks.cmd` green for any Core changes. Do NOT commit — Orchestrator
  reviews.

## Notes
- This is the client-perf half; verification is partly **visual** (Godot), so it is isolated from S36a's
  headlessly-testable wire/bandwidth half on purpose.
- Watch chunk node churn (don't thrash create/free at a boundary — add hysteresis like the AOI spawn path).
