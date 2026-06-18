# S36 — Terrain AOI: send only nearby blocked tiles (stop shipping the whole map to every client)

Severity: should-fix for big worlds (scalability). Surfaced by the 1000² test.

## Why

`ZoneInfo` ships **every** blocked tile in the map to **every** client at login
(`GameServer` builds `ZoneInfoMessage(_zone.Id, Width, Height, blockedTiles)` from the full
`Zone.BlockedTiles`). Blocked tiles are dominated by the map border (`~4×width`): 128²≈512,
**1000²≈4000, 2048²≈8200**. Measured: the 1000² border ~**doubled outbound bandwidth** (2.4→4.6 Mbps
in a 120-client stress) purely from login `ZoneInfo`. This does not scale with world size.

## What

Send blocked tiles **AOI-locally** instead of the whole map — mirror the entity AOI for terrain:
- At login, send only blocked tiles within an interest radius of the player's spawn (radius ≥ entity
  AOI radius + a margin so walls are visible before you reach them).
- As the player moves, stream blocked tiles for newly-entered regions. Track per-client
  already-sent terrain coarsely (e.g. by fixed-size tile **chunks**: send a chunk's blocked tiles the
  first time the player's AOI overlaps it).
- Likely needs a small terrain-chunk message (or repeated partial `ZoneInfo`); bump the protocol
  version if the wire format changes (current is v12).

This is the meatier of the pair — it introduces per-client terrain knowledge + send-on-entry, paralleling
how entity spawns work. Keep it simple: chunk grid + "have I sent this chunk to this client" set.

## Files (server + protocol; client decode)
- `src/Mmo.Server/Runtime/GameServer.cs` (ZoneInfo build + per-client terrain send on AOI movement)
- `src/Mmo.Shared/...` protocol (terrain-chunk message if added; version bump)
- `src/Mmo.Client.Core` + `src/Mmo.Client.Godot` (decode + render incrementally-arriving blocked tiles)
- tests for chunk send-on-entry + no-resend.

## Acceptance
- Big-world (1000²/2048²) login `ZoneInfo`/terrain bandwidth is AOI-local, not whole-map; the
  120-client stress outbound no longer ~doubles vs 128².
- Walls render correctly as the player moves into new regions (no missing/late walls within view).
- `run-checks.cmd` green + a 120-client/60s stress. Do NOT commit — leave for Orchestrator review.

## Note
Independent of S35 (different files: S35 = Zone/ServerOptions spawn gen; this = GameServer/protocol/
client terrain). Run after S35 is reviewed to avoid concurrent server edits.
