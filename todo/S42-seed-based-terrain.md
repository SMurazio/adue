# S42 — Seed-based deterministic terrain (ship the map, not the tiles)

Severity: should-fix (scaling + correct content architecture). **Replaces the abandoned S36a**
(chunked streaming of static tiles). Optimization-track **gate #1**. See the 2026-06-19 decision in
`docs/terrain-and-map-design.md` and `docs/capacity-ladder-study.md` (the login-bandwidth finding).

## Why

Static terrain is **content, not state** — it should ship with the client, not stream every login.
Today `ZoneInfo` ships the entire blocked-tile set to every client; on big/dense maps that's the
multi-MB login burst S40 measured. The map is **procedural**, so the clean fix is to **ship the seed,
not the tiles**: the server sends a tiny descriptor, the client regenerates the identical map locally,
and the server keeps its authoritative copy. Login terrain cost becomes ~constant regardless of map
size or obstacle density. The server stays fully authoritative (movement is validated against its own
map; the client holding the map weakens nothing).

## Scope (this task = procedural seed distribution; NOT authored map files, NOT dynamic terrain)

1. **Shared deterministic generator** in `src/Mmo.Shared/` — a pure, deterministic function
   `(width, height, seed, genVersion) -> blocked tiles / TileGrid`. Move the current server-side map
   generation (perimeter border + the few hardcoded segments in `Zone`/`TileGrid`) into this shared
   code. **Determinism is the contract:** identical inputs MUST produce byte-identical output on client
   and server — use an explicit seeded PRNG / fixed algorithm, no platform- or culture-dependent
   behavior. `genVersion` lets the algorithm change later without a silent client/server mismatch.
2. **Server** — build its authoritative map from `(dims, seed, genVersion)` via the shared generator at
   startup (seed from config/`ServerOptions`, default stable). Stop building/sending the blocked-tile
   list. Keep movement validation against this map (unchanged).
3. **Protocol (bump from v13)** — `ZoneInfo` carries `dims + seed + genVersion + contentHash`, and
   **drops the blocked-tile payload**. (`contentHash` = a hash of the generated blocked set, for an
   integrity/drift check.)
4. **Client** — on `ZoneInfo`, regenerate the map locally via the **same shared generator** from
   `(dims, seed, genVersion)`; render from it. **Verify** the locally-generated map's hash equals the
   server's `contentHash`; on mismatch, log loudly (generator drift / tampering) — the server remains
   authoritative regardless, so this is a diagnostic/integrity gate, not a security dependency.
5. Both clients (Godot + web debug) regenerate-and-render instead of consuming a tile payload.

## Files
- `src/Mmo.Shared/` — the shared deterministic generator (+ hash helper); unit tests for determinism.
- `src/Mmo.Server/Runtime/Zone.cs` + `Configuration/ServerOptions.cs` — generate from seed; seed config.
- `src/Mmo.Shared/Protocol/` — `ZoneInfo` change + version bump + codec read/write.
- `src/Mmo.Client.Core/` + Godot + web — regenerate from seed, render, hash-check.
- Tests: generator determinism (same seed → identical tiles, repeatable); ZoneInfo round-trip; a
  server↔client parity test (server map == client-regenerated map / hashes match); login carries no
  tile payload.

## Acceptance
- **Login terrain bandwidth is ~constant** (a few bytes: dims+seed+genVersion+hash) regardless of map
  size — a 2048² map costs the same at login as 128². Re-measure vs the S40 spike: no full-map dump.
- Client-regenerated map is **identical** to the server's (parity/hash test). Determinism test passes.
- Server stays authoritative; AOI invariant and movement validation unaffected.
- `run-checks.cmd` green; protocol version bumped. **Godot visual "walls render from the seed" check is
  deferred to the Orchestrator/human.** 120/30s stress on 2048² shows flat login bandwidth. Do NOT
  commit — Orchestrator reviews.

## Notes / out of scope
- **Authored maps (future):** ship the map **file** with the client + a version/hash; server loads the
  same file; same `ZoneInfo` shape (id/version). A later task when authored content exists.
- **Dynamic terrain (future):** destructible/doors/player-built changes stream as AOI-gated state deltas
  — not here.
- **Salvage:** the superseded chunked-streaming impl is on `wip/s36a-chunked-streaming` — its Godot
  per-chunk render feeds **S36b** (render the locally-generated big map, culled by view); its RLE/chunk
  model may feed a future map-file format.
- May split if large: shared-gen + protocol + server (headless-verifiable) vs client regen + render
  (visual). Surface the split rather than guessing if it gets unwieldy.
