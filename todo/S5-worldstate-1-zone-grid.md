# S5 — WorldState Step 1: introduce `Zone` that owns the tile grid

Severity: should-fix (structural; first step of the WorldState/Zone extraction).
Plan: `docs/worldstate-zone-design.md` (Stage 1, first half). **Prerequisite: none** (but the queue
runs S4 before this).

## Goal

Introduce a `Zone` type that owns the world map, and have `GameServer` hold a `Zone` instead of a
bare `TileGrid`. This is a mechanical, **behavior-preserving** first step — no entity model yet, no
wire/behavior change.

- `Zone` owns: zone id, dimensions, the `TileGrid` (blocked tiles + walkability), and the spawn
  point(s). Move the existing `TileGrid` into the `Zone`.
- Movement/spawn validation (`IsWalkable`, step target checks, default-spawn resolution) goes
  through the `Zone`.
- `GameServer` references `_zone` instead of `_tileGrid`; behavior is otherwise unchanged.

## Scope fence (do NOT do here)

- No `WorldState` / `WorldEntity` yet — that's S6. Players are still session-derived in this step.
- No snapshot/AOI changes, no new entity kinds, no protocol change, no allocation work.

## Acceptance

- All existing tests pass **unchanged**.
- A 120-client/60s stress run shows the same AOI/bandwidth/tick behavior as before.
- `GameServer` no longer holds a bare `TileGrid`; the `Zone` owns the map and validation.
- `run-checks.cmd` green.
