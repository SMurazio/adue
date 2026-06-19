# S36b — Godot per-chunk terrain render + culling

Severity: should-fix for big/dense worlds. **Split from the original S36** (Orchestrator decision).
**Depends on S42** (seed-based terrain — the client now generates the map locally; this renders it).
See `docs/terrain-and-map-design.md`.

> Re-pointed after the 2026-06-19 terrain pivot: the source of the map is no longer streamed
> `TerrainChunk`s (that approach was abandoned — see `wip/s36a-chunked-streaming`) but the **locally
> generated** map from S42. This task is unchanged in spirit — subdivide that local map into render
> chunks and cull by view. Salvage the Godot per-chunk render from the wip branch if useful.

## Why

Even with terrain shipped cheaply (S42), the Godot client renders walls as **one global wall MultiMesh
with no culling**. With dense interior maps that is a ~hundreds-of-K-instance mesh (~millions of
tris/frame, mostly off-screen). Rendered primitives must be bounded by **view**, not by map size.

## Scope (THIS task = Godot client rendering only)

Replace the single global wall MultiMesh with **per-chunk rendering** that culls and frees independently.
No protocol/server changes — the map is already available locally (S42); this just subdivides it for
rendering.

1. **A dictionary of render chunks** keyed by `(chunkX, chunkY)`, each a fixed tile block of the
   locally-generated map; each chunk owns **its own wall MultiMesh / node**, built from that chunk's
   tiles.
2. **Cull per chunk:** a per-chunk node lets Godot frustum-cull automatically; additionally distance-cull
   chunks beyond the view/AOI (hide or free). Off-view chunks must not contribute draw primitives.
3. **Free chunks** that leave range and rebuild on re-entry (re-derive from the local map — no refetch).
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
- This is the client-perf half; verification is partly **visual** (Godot), so it is isolated from S42's
  headlessly-testable seed/bandwidth half on purpose.
- Watch chunk node churn (don't thrash create/free at a boundary — add hysteresis like the AOI spawn path).
