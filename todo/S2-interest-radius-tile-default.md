# S2 — Default interest radius (96) exceeds the 64-tile world, so AOI never culls by default

Severity: should-fix (config correctness)

## Problem

`MMO_INTEREST_RADIUS` defaults to `96` (`src/Mmo.Server/Configuration/ServerOptions.cs:33`), a value
carried over from the old continuous/float world. It is now interpreted in **tiles**
(`GameServer.DistanceSquared` uses `Tile.X/Y`). On the default 64×64 grid, 96 tiles exceeds the world
diagonal, so every client always sees every other client — AOI culling and the AOI-as-anti-cheat
boundary are effectively inert in the default and stress configurations (the 120-client run shows
`culled/s=0`). The AOI integration test still passes only because it sets radius `5` explicitly.

## Fix

- Change the default `MMO_INTEREST_RADIUS` to a sane tile value that is smaller than the world and
  large enough for local interaction — use `14`.
- Update `.env.example` to include the movement/world vars that are currently missing, with a note
  that the radius is in tiles: `MMO_WORLD_WIDTH_TILES`, `MMO_WORLD_HEIGHT_TILES`,
  `MMO_STEP_COOLDOWN_MS`, `MMO_INTEREST_RADIUS` (tiles), `MMO_MAX_VISIBLE_ENTITIES`.
- Update `docs/runbook.md` env-var list accordingly (note the tile unit).

## Acceptance

- With default config, a stress run where players spread out shows `culled/s > 0` (AOI actually
  culls). On a clustered spawn it may still be ~0 until they disperse — that's fine.
- AOI enter/leave integration test still passes.
- `run-checks.cmd` green.
