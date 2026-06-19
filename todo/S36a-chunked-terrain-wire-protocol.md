# S36a — Chunked terrain on the wire (server chunk source + protocol + client consumption)

Severity: should-fix for big worlds. **Split from the original S36** (Orchestrator decision: correctness/
bandwidth first, then client render perf in **S36b**). See `docs/terrain-and-map-design.md` and the
original rationale carried below.

## Why

`ZoneInfo` ships **every** blocked tile in the map to **every** client at login (GameServer builds
`ZoneInfoMessage(Id, Width, Height, blockedTiles)` from the full `Zone.BlockedTiles`). Measured: on a
1000² map the login handshake spikes to ~24 Mbps inbound / multi-Mbps server-out as the full border
(~4000 tiles) ships to all 120 clients (seen in the S35 scattered-spawn stress). Interesting maps add
**interior obstacles that scale with AREA** (~10% of 2048² ≈ 420K blocked tiles → ~2.5 MB/client at
login). The whole-map transfer must die; terrain must stream **by chunk within the player's AOI**.

## Scope (THIS task = wire protocol + server streaming + keep clients WORKING)

End-to-end chunked terrain transfer that leaves both clients functionally equivalent to today (walls
render correctly). The Godot **per-chunk render + culling optimization is S36b** — here the client may
still accumulate received chunks into its existing wall rendering (correctness, not cull perf).

1. **Chunking:** divide the map into fixed chunks (default 32×32 tiles, configurable). Chunk `(cx,cy)`
   covers tiles `[cx*32 .. cx*32+31] × [cy*32 .. cy*32+31]`.
2. **Protocol (bump version from current):**
   - `ZoneInfo` keeps world dims + **chunk size**; it **stops carrying the tile list**.
   - New `TerrainChunk(chunkX, chunkY, tileData)` — server→client. **Structure `tileData` as one value
     per tile (e.g. a byte)** so it extends from blocked/open (now) to tile-TYPES later WITHOUT a wire
     rewrite; **RLE-compress** mostly-open chunks. Choose a sensible delivery class (reliable ordered —
     structural terrain).
3. **Server:** a chunked terrain source returning a chunk's tile data on demand. **Per-client track sent
   chunks** (HashSet of chunk coords). At login + as the player's AOI moves into new chunks, send the
   newly-covered chunks. (Chunk *unload* — telling the client to drop far chunks — may be deferred;
   still track per-client sent set so it's easy to add.)
4. **Clients (consume, minimally):** update each client that renders terrain (web debug + Godot) to build
   its walls from **accumulated TerrainChunk** data rather than from `ZoneInfo`'s tile list. Rendering may
   stay as-is (e.g. one mesh rebuilt from received chunks) — **do not** do the per-chunk cull here.

**Scope clamp:** chunk the EXISTING blocked/open terrain (no new tile types yet), but structure
`tileData` to allow them later. Stream-in on AOI. Unload optional.

## Files
- `src/Mmo.Shared/Protocol/` — `TerrainChunk` message + `ZoneInfo` change, codec read/write, version
  bump, delivery-class choice. (RLE encode/decode helper if added, with unit tests.)
- `src/Mmo.Server/Runtime/` — chunked terrain source; per-client sent-chunk tracking; AOI-driven chunk
  send at login + on movement into new chunks.
- `src/Mmo.Client.Core/` and the Godot/web client glue — consume `TerrainChunk`, render walls from
  accumulated chunks (no cull optimization yet).

## Acceptance
- On a 2048² map seeded with interior obstacles, a client receives only chunks within its AOI at login
  (login/terrain bandwidth bounded by AOI, **independent of total map obstacle count**), and new chunks
  arrive correctly as the player moves (no missing walls in view).
- Login bandwidth on 1000²/2048² is flat regardless of obstacle density (re-measure vs the S35 baseline
  spike). **AOI invariant**: terrain chunks outside a client's AOI are never serialized to it (test).
- Both clients still render walls correctly (functional parity with today).
- `run-checks.cmd` green; protocol version bumped per convention; integration test for AOI-bounded chunk
  delivery. **120-client/30s stress** on 2048² shows terrain/login bandwidth flat. Do NOT commit —
  Orchestrator reviews.

## Notes
- RLE matters: mostly-open chunks must be cheap. Test the RLE round-trip.
- `tileData` = one byte/tile now, extensible to tile-types later — do not hard-bake "blocked bool".
- Independent of S35. **S36b depends on this** (it optimizes the Godot rendering of these chunks).
