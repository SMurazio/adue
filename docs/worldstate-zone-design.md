# WorldState / Zone Design

> **STATUS (updated 2026-06-19): IMPLEMENTED — this is now a historical design record, not a backlog.**
> `WorldState`, `WorldEntity`, `Zone` (owning the `TileGrid`), the `EntityKind` set (incl. `Resource`),
> the transient/durable split, the replication step, `ZoneInfo` map distribution, and write-behind
> periodic checkpoint all exist in `src/Mmo.Server/Runtime/`. Stage 1 (extraction), Stage 3 (non-player
> entities — placeholder + resource nodes via S38), and Stage 4 (ZoneInfo, now being chunked by S36a)
> are shipped; Stage 2's goal (no per-tick GC) is met — stress runs show `gc 0/0/0` at 120–400 clients
> (see `capacity-ladder-study.md`). Keep this doc for the rationale/decisions below; do **not** treat
> the staged plan as outstanding work. Remaining genuine follow-ups have their own todos (e.g. grid
> AOI = S41).

Design + decision record for extracting an explicit world model. It was the keystone item
(roadmap Phase 4, networking-design-plan §S1): every entity was *derived from a live session*,
which blocked all non-player content, kept the hot tick loop allocation-heavy (the source of the
tick-time spikes), and tangled simulation with serialization. This document was the plan; it has since
been executed in the staged order below.

## Why this is the gate

- **Content is impossible without it.** `EntityKind.Player` is the only kind that can exist because
  entities come from sessions. NPCs, static objects, resource nodes, doors — all blocked.
- **It removes the GC half of the tick spike.** The 33–42 ms periodic spikes are partly synchronized
  heartbeats (todo S3) and partly per-tick LINQ allocation in the session-derived snapshot path. A
  world model with reused buffers eliminates the allocation, which raises the scaling ceiling.
- **It quarantines the netcode surface.** Simulation and serialization are currently entangled in
  `GameServer`. A clean world model lets only the replication step touch the wire — the Overwatch
  "few systems touch netcode" lesson.

## Goals

- Decouple *world entities* from *connections*: the world owns entities; a session links to one.
- Make non-player entities possible (without yet adding AI or interactions).
- Kill per-tick allocation on the hot path (reused buffers, no LINQ in the tick).
- Move the tile grid and map ownership into a `Zone`.
- Preserve every verified behavior: AOI culling, spawn/despawn, changed-state + heartbeat snapshots,
  snapshot acks, anti-cheat invariant, crash-proofing, network-id recycling, persistence.

## Non-goals (explicitly out of scope for this work)

- No ECS framework. Data-oriented, yes; a component/system engine, no.
- No AI behavior, pathfinding, interactions, items, or combat — at most one inert placeholder entity
  to prove the model end-to-end (Stage 3).
- No multi-zone / spatial split. One `Zone` ("sandbox"). The design must merely *not preclude* a
  later split.
- No grid/spatial-hash AOI (D3) or delta compression (D1) — those stay metrics-gated.
- No full structure-of-arrays layout yet (see Data Layout decision).

## Target architecture

Realize the Albion object separation the architecture doc already targets:

- **`WorldEntity`** — a server-authority object owned by the world. Fields: a stable entity id and a
  channel-local `NetworkId` (rented from the existing `NetworkIdPool` for *all* entities, not just
  players); `EntityKind`; `TileCoord`; `Direction8 Facing`; `DisplayName`; optional `CharacterId`
  and owning session link (null for non-players); `StateRevision`; and a durability flag (see
  transient-vs-durable). Mutable; this is where movement and rules apply.
- **`WorldState`** — owns the entity table (id → `WorldEntity`): add / remove / lookup / enumerate.
  Provides reused scratch buffers for the tick so enumeration and snapshot building allocate nothing
  per tick. No networking knowledge.
- **`Zone`** — owns the `TileGrid` (move it here), the `WorldState`, the zone id, dimensions, and
  spawn point(s). Exposes intent-level operations: `Spawn`, `Despawn`, `TryStep(entity, direction)`
  (validates cooldown + walkability against its grid), and AOI enumeration. The `Zone` is the unit
  that could later become its own process (spatial split L1); keep its surface clean enough that
  this stays possible (no cross-zone in-memory pointers).
- **`ClientSession`** — now a *connection + input + auth* object that **links to its `WorldEntity`
  by id**. It keeps the per-viewer replication bookkeeping it already has (known entities, sent
  revisions, last-snapshot set, snapshot sequence/ack, heartbeat tick). A player = session +
  entity; an NPC = entity with no session.
- **Replication step** — the *only* code that touches the wire. Per session: ask the `Zone` for the
  viewer's visible entities, then emit spawn/despawn/snapshots from `WorldState` data using the
  session's replication bookkeeping. Moves the snapshot logic out of `GameServer`'s tangle into one
  clearly-bounded unit.

### Tick shape after extraction

```
poll network -> drain main-thread actions
tick:
  input system     : apply queued MoveStep to player entities via Zone.TryStep
  (future) ai system: update non-player entities      <- not built now, just leave the seam
  replication system: per session, AOI-select from Zone + emit spawn/despawn/snapshot
  persistence      : checkpoint durable entities (write-behind, see below)
```

### Transient vs durable state (folds in design-plan S2)

The `Zone` knows which entities are **durable** (players — persisted via `ICharacterRepository`) vs
**transient** (NPCs/objects — in-memory, lossy, recreated on boot). Persistence only ever touches
durable entities. This is also where write-behind/periodic checkpoint belongs (today positions
persist only on disconnect — a crash loses everything since login). Periodic checkpoint of durable
entities is a reasonable add here, but if it grows the change too much, split it to its own todo.

## Data layout decision

**Use an array-of-structs entity table with reused buffers / pooling now; defer structure-of-arrays
(SoA) until profiling demands it.** Rationale: the measured problem is *allocation* (GC pauses), not
cache misses — CPU has headroom at 120–150. Eliminating per-tick LINQ and reusing scratch buffers
captures the GC win at far lower risk than an SoA rewrite. Revisit SoA only if a profiler shows
cache locality is the bottleneck at higher entity counts. State this as a decision so the
implementer doesn't gold-plate.

## Staged migration (the robust part — each stage ships and is reviewed independently)

**Stage 1 — Introduce `WorldState`/`Zone`; players become entities; behavior-preserving.**
Move `TileGrid` into `Zone`. Represent each authenticated player as a `WorldEntity` in `WorldState`;
`ClientSession` holds its `EntityId`. Route movement through `Zone.TryStep` and snapshot selection
through `WorldState`. **No wire/behavior change** — snapshots still carry only players; the protocol
is untouched. Acceptance: all existing tests pass unchanged; a stress run shows the same AOI/
bandwidth behavior as today. This is the risky structural move done in isolation, with the existing
test suite as the safety net.

**Stage 2 — Kill per-tick allocation.** Make the replication step iterate `WorldState` with reused
buffers / pooled lists instead of per-tick LINQ (`Select`/`OrderBy`/`ToArray`/`ToHashSet`).
Acceptance: `run-checks` green; a 120-client/60s stress shows `tickMs max` materially lower than the
current ~33–42 ms (this is the GC-spike fix; pairs with todo S3 on the heartbeat side). Measure
before/after and report both.

**Stage 3 — Prove a non-player entity end-to-end.** Add one inert entity kind (a static object or a
stationary NPC placeholder) spawned by the `Zone` at boot — no AI, no interaction. Verify it spawns,
replicates via AOI, shows in the web client, and (if marked transient) is not persisted. Acceptance:
a test asserting a non-session entity appears in a client's spawns/snapshots; manual web check shows
it. This is the proof the decoupling actually works.

**Stage 4 — (separate change) `Zone` sends its map to clients.** Today the web client *duplicates*
the blocked-tile seed locally. Add a `ZoneInfo` message (dimensions + blocked tiles, or a compact
map encoding) sent at login so the client renders the server's actual map. This is a protocol
addition (version bump) and can be its own todo after Stages 1–3; list it but don't bundle it.

## Risks & how to de-risk

- **Regression of verified behavior** — mitigated by Stage 1 being strictly behavior-preserving with
  the existing AOI/snapshot/persistence tests as the gate. If a test needs changing in Stage 1,
  that's a red flag to scrutinize.
- **Network-id ownership** — ids must now be rented/recycled for *all* entities, not just on
  session disconnect. Ensure transient entities return their ids on despawn.
- **Replication bookkeeping** — keep it on the session for now (don't also move it in Stage 1);
  moving entity ownership and replication state at once is too much churn for one reviewable step.
- **Anti-cheat invariant** — "outside AOI ⇒ never serialized" must hold for all kinds, not just
  players. Keep/extend the existing test.
- **Scope creep into AI/interactions** — fenced above; Stage 3 is one inert entity, nothing more.

## How this sets up the future

- Interactions/combat (Phase 4+) attach to `WorldEntity` and validate via `Zone` (distance, and the
  tile-LOS query we discussed).
- AI gets a system slot in the tick that's already reserved.
- The spatial split (L1) becomes "more than one `Zone`," with entity hand-off at boundaries — the
  `Zone` surface is designed to allow it without rewriting gameplay.

## Suggested todo breakdown

Each stage becomes one or more `todo/` items when this is handed off:
`S-worldstate-stage1-extract`, `S-worldstate-stage2-allocation`, `N-worldstate-stage3-placeholder`,
`N-zoneinfo-map-distribution`. Stages 1–2 are `S` (structural + the GC/scaling fix); 3–4 are `N`.
