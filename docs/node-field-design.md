# Node Field — Harvestables at Forest Scale (PROPOSED, orchestrator, 2026-07-04)

**Status: PROPOSED — user-initiated ("I want thousands of them scattered... a very nature heavy
environment").** Replaces per-entity resource nodes with the industry static-catalogue + exception-list
architecture (the Albion model: positions are world data both sides share for free; only deviations
cross the wire). Supersedes procedural-population-design P3 (the scatter upgrade lands here, shared).

## 1. Why (the cost model)

Today's ~188 trees/rocks are full WorldEntities: EntitySpawn messages, AOI gather presence, snapshot
slots. At "nature heavy" scale (target 4,000–8,000 nodes) that architecture costs hundreds of entities
per AOI in a forest — per snapshot, per client, forever. The fix: an untouched tree must cost ZERO
network and ZERO tick — only memory in a shared table. Entities are for things that MOVE or think.

## 2. Decisions

**D1. The catalogue is deterministic SHARED data.** `NodeCatalog` (Mmo.Shared): computed at zone build
by BOTH sides from (zone seed, authored map) — authored T/R marker pins FIRST (stable low indices),
then P1 WeightedScatter per node class (density = category × away-from-road curve × patch noise — the
P3 upgrade, done here in shared code). Entry: {index (ordinal = the node's PERMANENT id), tile,
nodeType}. WHY: positions never cross the wire; a node is referenced by a ushort-sized index.

**D2. Drift guard: catalogueHash rides ZoneInfo (protocol v46).** FNV over the catalogue (same
discipline as the map ContentHash), hard-fail on mismatch — a client whose scatter code drifted from
the server's must not see trees where the server has none. (The map hash can't cover this: the
catalogue depends on shared CODE, which the protocol version only partially pins.)

**D3. Server state = two tiny arrays, no entities.** `NodeField` (server): per index {depleted bool,
respawnAtTick uint}. Scattered nodes are NO LONGER WorldEntities (no spawn messages, no AOI presence,
no snapshot slots). Respawn = a per-tick sweep over a small due-list (or cheapest: check-on-harvest +
a coarse periodic sweep — implementer's call, documented). House/Portal PROPS and all other entity
kinds are untouched — only harvestable nodes move.

**D4. Exceptions broadcast GLOBALLY, not AOI-scoped.** A node state flip (harvested/respawned) is one
reliable `NodeStateMessage {index, depleted}` (~5 bytes) to ALL clients; login sends the full current
exception list (`NodeStateBatch` — only DEPLETED indices, typically dozens). WHY global: at community
scale (200 players) harvest events are player-paced and tiny — per-session known-sets and AOI diffing
would be complexity with no payoff (the SpawnerMarker pattern exists for BIG payloads; 5 bytes isn't
one). Bonus: every client's map of depletion is world-complete — future ecology/legibility food.

**D5. Harvest targets an INDEX.** New client→server `HarvestNodeMessage {index}` replacing the entity
Interact path for nodes (Interact stays for corpses/props). Server validates: index in range, node
available, player within the SAME interaction range as today (vs the catalogue position). Loot/
inventory flow unchanged (same gather tables keyed by nodeType).

**D6. Client renders the catalogue like the floor, not like entities.** Chunked MultiMesh per node
type (the M2 bulk-buffer path; the actual tree/rock meshes, one draw per type per chunk, frustum-
culled). Depleted swap: per-chunk membership move between an "available" and a "depleted/stump"
MultiMesh (rebuilding one 32-tile chunk's buffer on a state flip is microseconds). Nameplates: NONE at
field scale — the label moves to a proximity/hover affordance later if missed (a forest of "Tree"
labels is noise). Pick/harvest: nearest catalogue node within reach of the click, revalidated
server-side.

**D7. Depletion does NOT persist across restarts** (matches today; respawn timers are minutes — a
restart forgiving stumps is acceptable and cheap). Revisit only if ecology later couples flora.

**D8. Nature-heavy content targets.** Total ~5,000 nodes on the 384² map: trees dominant (~70%),
rocks ~20%, plants ~10%; density curves make wings/Verge read FOREST (thick clusters via patch noise)
while town/roads/arenas stay playable-open (road suppression + arena-rect exclusion via the same
candidate predicate that already excludes non-grass). Gather-table content unchanged this arc.

## 3. What this is NOT

No collision change (nodes don't collide today; still don't). No new gather content/loot. No
persistence. No per-node HP (one-hit-per-charge harvest exactly as today). No LOD/imposters yet —
MultiMesh at 5k instances is well under the decor layer's proven 27k. Props/corpses/monsters untouched.

## 4. Tasks

- **N1 — shared NodeCatalog + hash (sonnet):** catalogue build (pins + WeightedScatter, per-class
  params), catalogueHash, determinism/distribution/pin-stability tests. No wire/server/client changes.
- **N2 — protocol v46 + server NodeField (sonnet, then FABLE review with N1 — hash + protocol tier):**
  ZoneInfo catalogueHash field, NodeState/NodeStateBatch/HarvestNode messages, NodeField state +
  respawn, harvest validation, login batch, REMOVAL of the scattered-entity spawn path (props/pins as
  entities die; pins live in the catalogue now), codec + live-server tests (harvest→broadcast→late-join).
- **N3 — client field rendering + interaction (sonnet):** catalogue MultiMesh chunks + depleted swap,
  click-pick + HarvestNode send, remove node-entity visual expectations, zone-build timing print
  extension. Live walk verifies.
- **N4 — density/content pass + the nature-heavy feel walk (orchestrator + user):** tune D8 targets on
  foot; the world should READ as wilderness with civilization carved out of it.

Order: N1 → N2 → N3 → N4. One combined Fable review after N2 (catalogue determinism + protocol are the
irreversible surfaces); N3 rides the user walk.
