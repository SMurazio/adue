# N7 — WorldState Step 4: prove a non-player entity end-to-end

Severity: nit-tier but high-value (the payoff that proves the decoupling works).
Plan: `docs/worldstate-zone-design.md` (Stage 3). **Prerequisite: S6** (entity model exists).

## Goal

Add **one inert non-player entity** to prove the world model actually supports content — no AI, no
interaction, no behavior. A static object or a stationary NPC placeholder, spawned by the `Zone` at
boot.

- Spawn one non-player `WorldEntity` (an existing or new `EntityKind`, e.g. a static object) at a
  fixed walkable tile when the `Zone` initializes.
- It is **transient** (durability flag false) — not persisted; recreated on boot.
- It must flow through the normal pipeline: rented `NetworkId`, reliable `EntitySpawn`, AOI
  selection, snapshots, despawn when out of interest — exactly like a player, but with no session.

## Scope fence (do NOT do here)

- No AI, pathfinding, movement logic, or interaction. It just exists and replicates.
- One entity (or a small fixed handful) — not a spawner system.

## Acceptance

- A test asserts a non-session entity appears in a client's `EntitySpawn` / snapshot stream and is
  AOI-culled like any entity.
- Manual web check: the placeholder renders in the client at its tile.
- It is not written to the database (verify it's absent from persistence).
- `run-checks.cmd` green.
