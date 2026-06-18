# S8 — Finish the decoupling: remove duplicated position state from ClientSession

Severity: should-fix (code health / latent-bug risk; completes what S6 intended). Not a current bug.

## Problem

After the WorldState extraction, position now lives authoritatively on `WorldEntity`, and movement
correctly applies there (`GameServer` MoveStep → `_zone.TryStep(entity, …)` →
`session.SyncFromEntity(entity)`; snapshots read entities). **But `ClientSession` still carries a
full duplicate of that state** — `Tile`, `Facing`, `StateRevision`, `_lastStepTick`, and an entire
`TryStep(...)` — kept in sync via `SyncFromEntity`.

Consequences:
- **Dead production code:** `ClientSession.TryStep` and the `Zone.TryStep(ClientSession, …)` overload
  are never called in production (movement uses the `WorldEntity` overload). They're kept alive only
  by `ClientSessionTests`, which therefore test a dead path → **false coverage**.
- **Two sources of truth** for a player's position that must be hand-synced; if anyone later wires
  the dead `ClientSession.TryStep` path, the session and entity silently diverge.
- The session's mirrored `Tile/Facing/StateRevision` are mostly unread (snapshot/AOI/revision logic
  all read the `WorldEntity`).

## Fix

- Remove `Tile`, `Facing`, `StateRevision`, `_lastStepTick`, `TryStep`, and `SyncFromEntity` from
  `ClientSession`. Keep the `EntityId` link, auth/connection state, and the per-viewer replication
  bookkeeping (known entities, sent revisions, last-snapshot set, snapshot seq/ack, heartbeat phase).
- Delete the now-unused `Zone.TryStep(ClientSession, …)` overload.
- Anywhere that read `session.Tile`/`Facing` (e.g. `SaveTileBestEffort` fallback) reads the
  `WorldEntity` via `TryGetSessionEntity`.
- Repoint `ClientSessionTests` step/cooldown/blocked/shared-tile cases at `WorldEntity.TryStep`
  (the real path) — or move them to a `WorldEntityTests`.

## Acceptance

- A player's position exists in exactly one place (`WorldEntity`); `ClientSession` holds no tile.
- No dead `TryStep` paths remain; step/cooldown/walkability tests exercise the real path.
- All existing behavior preserved; `run-checks.cmd` green; a stress run is unchanged.
