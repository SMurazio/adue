# S38 — Resource nodes + Interact/harvest (server + protocol)

Severity: should-fix. The gather verb on top of the inventory foundation. **Depends on S37.** See
`docs/gather-and-inventory-design.md` and `docs/feature-roadmap.md` Phase 4 ("server-validated
interactions: target entity, validate distance, emit result").

## Scope (this task = server + protocol ONLY)

No client UI/rendering (that's **S39**). No crafting. Build the resource entities, the generic
interaction verb, server-authoritative resolution, respawn, and replication — verifiable via
integration tests without the Godot client.

## What

1. **Resource node definitions + registry** — type → `{ yields itemType + quantity, respawnTicks }`.
   Seed 2–3 node types matching S37 items (e.g. Tree→Wood, Rock→Stone, Plant→Fiber). Code registry;
   adding a node type = a registry entry.
2. **Resource nodes as server-owned world entities** (`EntityKind.Resource`) — placed in the zone,
   NOT derived from sessions. Scatter a handful near spawn for now (a small placement/scatter helper is
   fine; full map population is out of scope). Node **transient state**: `Available` vs `Depleted` +
   `respawnAtTick` — **server-memory only, NOT persisted** (respawns fresh on restart).
3. **Protocol (bump version from v12)**:
   - `InteractRequest(targetNetworkId)` — client→server, generic.
   - `InteractResult(success, reason)` — server→client (owner).
   - `InventoryUpdate(changed stacks)` — server→client, **owner only**.
   - Resource availability replicates via the **existing AOI entity-state path** (add a depleted flag /
     treat depletion as a state change). Stay AOI-gated — node state only reaches clients that can see it.
4. **Server-authoritative resolution** of `InteractRequest`:
   - Validate: authenticated; target exists and is in the requester's AOI; **distance ≤ 1 tile**
     (adjacency); target is a `Resource`; node `Available`.
   - On success: grant the yield via the **S37 inventory service** → mark node `Depleted` + set
     `respawnAtTick` → send `InteractResult(success)` + `InventoryUpdate` to the owner; node-state change
     replicates by AOI.
   - On failure (too far / not a resource / depleted / unauthed): `InteractResult(false, reason)`, no
     state change. Rate-limit / validate like other client input (see Phase 1 validation rules).
   - On the respawn tick: node returns to `Available` (replicates by AOI).

## Files (server + protocol; no client)
- `src/Mmo.Shared/Protocol/` — `InteractRequest` / `InteractResult` / `InventoryUpdate` messages,
  codec read/write, version bump, delivery-class choice (reliable for these structural events).
- `src/Mmo.Server/Runtime/` — resource node defs + placement; interact handling + validation; respawn
  on tick; wire to the S37 inventory service; AOI node-state replication.
- Tests (integration, no Godot client): adjacency validation (reject too-far / unauthed / non-resource /
  depleted); successful harvest grants the item + depletes; respawn restores availability; **AOI
  invariant** — node state never serialized to a client that can't see it.

## Acceptance
- A player adjacent to an `Available` resource node can `Interact` → the yielded item appears in their
  (S37-persisted) inventory, the node depletes, then respawns after `respawnTicks`.
- Out-of-range / depleted / non-resource / unauthenticated interactions are rejected with a reason and
  cause no state change.
- Protocol version bumped; AOI invariant covered by a test.
- `run-checks.cmd` green + a 120-client/30s stress (watch interaction handling doesn't blow the tick
  budget). Do NOT commit — Orchestrator reviews.

## Review finding carried from S37 (must handle here — harvest makes inventory mutable)

S37 attaches the inventory to the durable `WorldEntity` and loads it from the DB on login. The
account-**takeover** path (`GameServer` `KickExistingSessionForCharacter`) hands off the kicked
session's **tile** (`takeoverTile`) but the new session's inventory is **re-read from the DB**, which is
stale vs the kicked session's not-yet-flushed in-memory inventory. This was harmless in S37 (inventory
was never dirty — no mutation path). **Once harvest makes inventory mutable, this is a data-loss race**
(a fresh login by the same account mid-session reloads pre-harvest inventory and can later overwrite the
flushed gains). Fix it as part of this task: on takeover, **hand off the kicked entity's in-memory
`Inventory`** to the new entity (mirroring `takeoverTile`) rather than using the DB-loaded stacks — or
flush-then-reload synchronously. Add a regression test for harvest → same-account relogin (takeover) →
inventory intact.
