# S36 — Chunked terrain streaming (stop shipping the whole map; foundation for interesting maps)

Severity: should-fix for big worlds; prerequisite for interior-obstacle / authored maps. Surfaced by the
1000²/2048² tests + the "more interesting maps" direction.

## Why

`ZoneInfo` ships **every** blocked tile in the map to **every** client at login
(`GameServer` builds `ZoneInfoMessage(Id, Width, Height, blockedTiles)` from the full
`Zone.BlockedTiles`; client builds one global wall MultiMesh with no culling).

- Border-only that's ~8K tiles at 2048² (~2× the 1000² login cost) — a nuisance.
- But **interesting maps add interior obstacles that scale with AREA**: ~10% of 2048² ≈ **420K blocked
  tiles** → ~2.5 MB per client at login **and** a 420K-instance uncull ed client MultiMesh (~5M tris/frame,
  mostly off-screen). Both the whole-map transfer and the single global wall mesh break with rich maps.

Fix it once, properly: stream terrain **by chunk within the player's AOI**, render **per-chunk with
culling**. This handles border-only AND dense interior maps and is the foundation for the map roadmap.
See `docs/terrain-and-map-design.md` for the broader model + open decisions.

## Design (first cut — keep bounded)

- **Chunking:** divide the map into fixed chunks (default 32×32 tiles, configurable). Chunk `(cx,cy)`
  covers tiles `[cx*32 .. cx*32+31] × [cy*32 .. cy*32+31]`.
- **Server:** a chunked terrain source that returns a chunk's tile data on demand. Per-client, track
  **sent chunks** (HashSet of chunk coords). At login + as the player's AOI moves into new chunks, send
  them. (Chunk *unload* — telling the client to drop far chunks — can be deferred, but render per-chunk
  so it's easy to add.)
- **Protocol (bump from v12):** a `TerrainChunk` message (`chunkX, chunkY, tileData`). `ZoneInfo` keeps
  world dims + chunk size; tiles arrive via `TerrainChunk`. **Structure `tileData` as one value per tile
  (e.g. a byte) so it can extend from blocked/open (now) to tile-TYPES later WITHOUT a wire rewrite**;
  RLE-compress mostly-open chunks.
- **Client:** a dictionary of loaded chunks; **each chunk renders its own walls (own MultiMesh/node)** so
  chunks cull + free independently. Replace the single global wall MultiMesh. Frustum/distance-cull
  chunks (a per-chunk node lets Godot cull; or cull manually by distance).

**Scope for THIS task:** chunk the EXISTING blocked/open terrain (no new tile types yet — that's the
design-note evolution, but structure `tileData` to allow it). Stream-in on AOI + render-per-chunk with
culling. Unload optional.

## Acceptance

- On a 2048² map seeded with interior obstacles, a client only receives chunks within its AOI at login
  (login/terrain bandwidth bounded by AOI, **independent of total map obstacle count**), and new chunks
  arrive correctly as the player moves (no missing walls in view).
- Client rendered primitives are bounded by **view**, not by map size (off-view chunks culled).
- A 120-client/60s stress on 2048² shows terrain/login bandwidth flat regardless of obstacle density.
- `run-checks.cmd` green; protocol version bumped per convention. Do NOT commit — Orchestrator reviews.

## Notes
- Meaty: server streaming + protocol + client chunk rendering. **May split** into S36a (server+protocol
  chunk streaming) and S36b (client per-chunk render+cull) if one task is too large.
- Independent of S35 (S35 = spawn gen in Zone/ServerOptions; this = GameServer/protocol/client terrain).
