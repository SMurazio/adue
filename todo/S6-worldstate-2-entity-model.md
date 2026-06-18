# S6 — WorldState Step 2: entity model; players become world entities

Severity: should-fix (structural; the core of the extraction — the riskiest step).
Plan: `docs/worldstate-zone-design.md` (Stage 1, second half + Data Layout + object separation).
**Prerequisite: S5** (Zone owns the grid).

## Goal

Decouple world entities from connections. Introduce a world-owned entity table and represent each
authenticated player as an entity that its session links to. **Behavior-preserving**: snapshots
still carry only players, and the protocol is unchanged.

- `WorldEntity` (server-authority object): a stable entity id + a channel-local `NetworkId` rented
  from the existing `NetworkIdPool`; `EntityKind`; `TileCoord`; `Direction8 Facing`; `DisplayName`;
  optional `CharacterId` + owning-session link (null for non-players); `StateRevision`; and a
  **durability flag** (players = durable, persisted; future non-players = transient).
- `WorldState` (owned by the `Zone`): the entity table (id → `WorldEntity`) with add / remove /
  lookup / enumerate. (Reserve the seam for reused scratch buffers — the allocation work itself is
  S7, not here.)
- On login, create a `WorldEntity` for the player and store its id on the `ClientSession`. On
  disconnect, remove the entity and return its `NetworkId` to the pool.
- Route movement (`MoveStep` → apply to the player's entity via the `Zone`) and AOI/snapshot
  selection through `WorldState` enumeration instead of iterating `_sessions`.

## Decisions (from the design doc — don't deviate without surfacing it)

- **Array-of-structs entity table now; NOT structure-of-arrays.** The measured problem is allocation
  (GC), not cache misses; SoA is deferred until profiling demands it.
- **Keep per-viewer replication bookkeeping on the `ClientSession`** for this step (known entities,
  sent revisions, last-snapshot set, snapshot seq/ack, heartbeat phase). Moving entity ownership
  AND replication state in one step is too much churn for one reviewable change.
- `NetworkId` is now rented/recycled for entities, conceptually for all entities (only players
  exist yet).

## Scope fence (do NOT do here)

- No non-player entities (that's S7… no — that's N7-worldstate-4). No allocation optimization (S7).
  No SoA. No protocol/wire change. No AI/interactions.

## Acceptance

- **All existing tests pass unchanged.** If a test needs changing to pass, that is a red flag that
  behavior shifted — stop and surface it rather than editing the test.
- AOI enter/leave, spawn/despawn, changed-state + heartbeat snapshots, snapshot acks, the
  AOI-as-anti-cheat invariant, network-id recycling, and persistence all behave as before.
- A 120-client/60s stress run is comparable to the current branch.
- `run-checks.cmd` green.

## If this gets too big

If the change balloons, split it (e.g. "add `WorldState`/`WorldEntity` types + populate alongside
sessions" then "switch the read paths to `WorldState`") into new `todo/` files and surface that,
rather than landing one giant unreviewable commit.
