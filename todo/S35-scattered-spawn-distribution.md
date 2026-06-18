# S35 — Scattered spawn distribution (spread players across the world so AOI actually filters)

Severity: should-fix. Enables a real AOI evaluation.

## Why

`Zone.CreateSpawnTiles` (SpawnDistribution.Distributed) spreads spawn tiles only within a **fixed
±32-tile patch around map center** (`spreadTiles=32`, `spacing=4`). So on ANY map size, all clients
spawn into that small central patch → everyone is within the 40-tile AOI radius → AOI filters almost
nothing. Measured: even on a **1000×1000** world, `visible avg ≈ 74` of 120 (unchanged from 128²).
The map size is irrelevant to clustering; the spawn spread is hard-coded small.

## What

Add a `SpawnDistribution.Scattered` that distributes spawn tiles **across the whole walkable map**,
not a fixed central patch:
- In `Zone.CreateSpawnTiles`, for `Scattered`, generate spawn tiles spanning the map (e.g. a grid with
  spacing scaled to map dimensions, or evenly across the walkable area), skipping blocked tiles.
- Wire it through `ServerOptions.ReadSpawnDistribution` (`MMO_SPAWN_DISTRIBUTION=scattered`) and the
  `SpawnDistribution` enum. Keep `Distributed` and `Clustered` unchanged.

## Files (server only)
- `src/Mmo.Server/Runtime/Zone.cs` (CreateSpawnTiles + enum if it lives here)
- `src/Mmo.Server/Configuration/ServerOptions.cs` (ReadSpawnDistribution case + the enum)
- a unit test that `Scattered` produces spawn tiles spanning the map (min/max X,Y near map edges, not
  just center ±32).

## Acceptance
- `MMO_SPAWN_DISTRIBUTION=scattered` on a large map produces spawn tiles spread across the map.
- A 120-client/30s stress on a 1000² world with scattered spawns shows `visible avg` drop sharply
  (AOI now filtering) vs the ~74 baseline. (Orchestrator will run this.)
- `run-checks.cmd` green. Do NOT commit — leave for Orchestrator review.
