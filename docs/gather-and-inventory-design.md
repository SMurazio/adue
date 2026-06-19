# Gather & Inventory Design — the first gameplay loop

Status: design note / decision record. The first gameplay loop: *walk to a resource node → Interact →
item lands in inventory → node depletes and respawns*. Forces every foundational gameplay system
(entity state beyond position, a server-authoritative verb, owned per-player durable state) while
staying small. Crafting layers on later.

Principle: build the **seams** (definition/instance split, registry, inventory service, durable/transient
split, owner-scoped replication); keep first **content** tiny (~3 stackable resources, no unique items,
no recipes). "Production-ready via open seams, not building everything now."

## Roadmap alignment

On-roadmap, not ahead of it. `feature-roadmap.md` Phase 4 (Gameplay Foundations):
- Inventory/items were deferred until "login/session/persistence is stable" (Phase 3) — now passed.
- "Add server-validated interactions: **target entity, validate distance, emit result**" → the generic
  `Interact` verb below.
- "**Classify state as transient/lossy versus durable-contract** before adding complex entity types" →
  the durable/transient split below.
- "resource node" is a named planned entity kind; inventory follows the existing tile rule:
  **server-memory truth + write-behind persistence, never DB in the tick hot path**.

## Core model: definitions vs instances

The decision that makes items scale — split the static catalog from per-player holdings:
- **Item definition (template)** — static catalog entry: stable `itemType` key/id, display name, max
  stack, category, properties. Lives in an **item registry** (code registry now; data files later).
  Immutable, shared, resolved client-side from the key.
- **Item instance (stack)** — what a player owns: compact `(templateId, quantity)`. Reserve an
  *optional* unique instance-id field for future non-stackable uniques (equipment w/ durability) —
  present in the type, unused for stackables.

Adding an item = adding registry data, not code. Inventory stores tiny refs; the wire carries
`(templateId, qty)`; the client resolves the template locally.

## Durable vs transient (decides persistence)

- **Inventory = durable contract** → server-memory truth + **write-behind** persistence (mirror
  `SaveTileAsync`). Stored **normalized**: a `character_items` table (`character_id`, `template_id`,
  `quantity`), not a JSON blob — it's the table that grows into trade/bank/auction and needs atomic
  mutations.
- **Resource-node state (available/depleted + respawn timer) = transient/lossy** → server-memory only,
  **not persisted**; respawns fresh on restart. Only a respawn timer touches the tick, never the DB.

## Server-authoritative inventory service

All add/remove flows through one validated path (atomic, dupe-proof — trivial now, essential once
trade exists). No inventory mutation anywhere else.

## Interaction verb (generic)

Generic `Interact(targetNetworkId)`, not harvest-specific, so it later covers talk/open/use:
`Interact` → validate (authed, target in AOI, **distance ≤ 1 tile**, target is a harvestable Resource,
available) → grant item via the inventory service → deplete node + schedule respawn → reply
`InteractResult` + owner `InventoryUpdate`. Harvest is just the first dispatch target.

## Replication scoping

- **Inventory → owner only** (private; `InventoryUpdate` to that client).
- **Resource-node availability → AOI** (like other entity state; AOI stays a security boundary).
- Keep "private inventory" and (future) "visible equipment" as separate seams so visible gear can
  AOI-replicate later without redesign.

## Protocol

New messages (bump version from v12): `InteractRequest` (client→server), `InteractResult` +
`InventoryUpdate` (server→client). Resource availability rides the existing AOI entity-state path
(add a depleted flag / treat depleted as a state change).

## Decisions resolved
- Persistence: **normalized `character_items`** (not a blob).
- Instances: **stackables-only `(templateId, qty)`** with a reserved-but-unused unique-id seam.
- Verb: **generic `Interact(targetId)`**.
- Scope: **gather now, craft later**; runs on the current 128² map.

## Deferred (explicit non-goals for now)
Crafting/recipes; unique items & equipment/durability; item data-files (stay a code registry);
inventory capacity/weight rules (keep a simple cap or none); banks/trade/auction.

## Sequencing
1. **S37 — item & inventory foundation**: definitions + registry, instance/stack, inventory service,
   `character_items` table + write-behind load/save. No verbs, no UI.
2. **S38 — resource nodes + Interact/harvest**: node defs + scatter, `Interact` protocol + server
   resolution + respawn, owner `InventoryUpdate`, AOI node-state replication. Server + protocol only.
3. **S39 (next) — client gather UX**: render nodes, harvest input, inventory panel, feedback,
   depleted visuals. Makes the loop playable end-to-end.
4. Later: crafting, then unique items/equipment.

S37 and S38 are independently verifiable server-side (unit + integration tests) before the client lands.
