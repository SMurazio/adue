# N8 — WorldState Step 5: server sends its map to clients (`ZoneInfo`)

Severity: nit-tier (additive). Plan: `docs/worldstate-zone-design.md` (Stage 4).
**Prerequisite: S5** (Zone owns the map). Independent of S6/S7, but do after them to avoid churn.

## Problem

The web client currently **duplicates** the server's blocked-tile seed locally
(`src/Mmo.Client.Web/wwwroot/app.js`). The server never tells clients the map, so the moment the
world is anything other than the one hardcoded layout, the client renders the wrong thing.

## Goal

Add a `ZoneInfo` message the server sends at login carrying the map: grid dimensions + blocked tiles
(a compact encoding is fine — e.g. a bitset or run-length, not a per-tile list if it's large). The
client renders the **server's** map and stops using its local seed.

- New `MessageType.ZoneInfo` + message record + codec encode/decode (this is a **protocol change →
  bump `ProtocolCodec.Version`**).
- Server sends `ZoneInfo` reliably right after `LoginResult` (or alongside it).
- Web client builds the tile grid / walls from `ZoneInfo`; remove the duplicated blocked-tile seed.

## Scope fence

- Static map only — no dynamic/runtime map editing or streaming chunks. One zone.

## Acceptance

- Web client renders dimensions + walls from the server (delete the local seed); changing
  `MMO_WORLD_WIDTH_TILES` / wall layout server-side is reflected in the client with no client edit.
- Protocol round-trip test for `ZoneInfo`; `docs/protocol.md` updated and version bumped (and not
  described as "planned").
- `run-checks.cmd` green.
