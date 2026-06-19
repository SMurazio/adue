# Terrain & Map Design — chunked world, tile types, authoring

Status: design note / decision record. Captures the model behind chunked terrain (S36) and the open
decisions for "more interesting maps." Principle: build the **chunk seam** now (reversible, extensible);
defer tile-types and the authoring pipeline until needed — don't build it all up front.

## Current state (and why it doesn't scale to interesting maps)

- `TileGrid` = `Width`, `Height`, and a `HashSet<TileCoord>` of **blocked** tiles. `IsWalkable` is an
  O(1) bounds + set lookup. Memory is O(blocked), not O(area).
- Default map = border (`~4×width`) + 3 tiny hardcoded segments. So today blocked tiles are
  **perimeter-only** (~8K at 2048²).
- `ZoneInfo` ships the **entire** blocked set to **every** client at login; the client builds **one**
  global wall MultiMesh with **no frustum culling**.
- This is cheap only because walls are perimeter-only. Interior obstacles that scale with **area** (e.g.
  10% of 2048² ≈ 420K tiles) blow up both the login transfer (~2.5 MB/client) and the uncull ed wall mesh
  (~5M tris/frame). Runtime (movement O(1), AOI O(players²)) is unaffected — obstacles are terrain, not
  entities.

## Chunk model (the seam — S36)

- Fixed chunks (default **32×32** tiles; configurable). A chunk is the unit of streaming, rendering,
  culling, and authoring.
- **Server** holds/produces per-chunk tile data; streams chunks within a player's AOI (send on AOI entry,
  optionally drop on AOI exit), tracked per-client.
- **Wire:** a `TerrainChunk` message (`chunkX, chunkY, tileData`), RLE-compressed for sparse chunks.
  `ZoneInfo` carries world dims + chunk size only.
- **Client** keeps loaded chunks in a map, renders each as its own node/MultiMesh, culls/frees by
  distance/visibility. Primitives bounded by view, not map size.

This one mechanism covers border-only AND dense maps, and is the natural unit for everything below.

## Tile representation (evolution, post-S36)

- Today: blocked / open (1 bit). **Structure the chunk `tileData` as one byte per tile from the start** so
  it extends to tile **types** (e.g. `Open, Wall, Water, Floor, Door, ...`) without a wire rewrite.
- Per-chunk byte array = 1 KB at 32². A 2048² world fully loaded = 4096 chunks × 1 KB = ~4 MB server-side
  (fine); clients hold only loaded chunks. RLE keeps mostly-open chunks tiny on the wire.
- Walkability becomes "is this tile type passable" instead of "is it in the blocked set."

## Authoring (the key open decision)

How interesting maps get *made*. Options (not mutually exclusive):

1. **Procedural** (seed + noise → terrain/obstacles). No content files, deterministic, effectively
   infinite, least pipeline work. Good for wilderness. Server can generate chunks on demand.
2. **Authored** (a map editor / [Tiled](https://www.mapeditor.org) `.tmx` import / custom format → chunk
   files on disk). Hand-crafted towns/dungeons; needs an editor + import + storage.
3. **Hybrid** — authored key areas (towns, dungeons) over procedural fill. Common MMO approach.

Lowest-effort first step for "interesting maps" is **procedural** (no editor needed); authored content can
layer on once the chunk pipeline exists.

## Decisions needed (Orchestrator + user)

- Chunk size (32² default — revisit vs streaming granularity / message size).
- Tile-type set + when to introduce it (S36 stays blocked/open; types are a follow-up).
- Authoring approach: procedural-first vs. invest in an editor/import early.
- Chunk **unload** policy (bound client memory on huge worlds) — deferrable.

## Sequencing

1. **S36** — chunked streaming of the *current* blocked/open terrain (the seam). Per-chunk client render + cull.
2. Tile **types** (extend `tileData`; type-driven walkability + client visuals).
3. Authoring pipeline (procedural generator and/or editor import) per the decision above.

Each step is independently shippable; step 1 is the only one that unblocks big interesting worlds.
