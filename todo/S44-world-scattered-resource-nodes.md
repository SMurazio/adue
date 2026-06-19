# S44 — Scatter resource nodes across the whole world (replace the spawn-origin cluster)

Severity: should-fix. The S38 placeholder placed **3 nodes clustered at `_spawnTiles[0]`** ("a handful
near spawn" — explicitly a stand-in). A play-test exposed the gap: a player who loads anywhere but that
one spot sees **no harvestable nodes at all**. Replace it with a real world-wide scatter so the gather
loop is discoverable wherever a player is. This is the **node-placement-as-content** fix flagged in the
wrong-model audit.

## Scope (server-side only — NO protocol/client changes)

Resource nodes are server-owned `EntityKind.Resource` entities replicated via the existing AOI snapshot
path; the client (S39) already renders whatever it's sent. So this is purely **server placement** — no
wire or client change.

1. **Deterministic world-wide scatter.** Replace `Zone.PlanResourceNodeScatter` (the `_spawnTiles[0]` +
   fixed-offsets cluster) with placement that distributes nodes **across the whole walkable map**:
   - **Deterministic** from the map seed (derive a node-placement seed from it, e.g. `seed ^ const`), using
     an explicit seeded PRNG — so restarts regenerate the **same** layout (nodes aren't persisted, so a
     deterministic layout keeps the world consistent across restarts; the tree stays where it was).
   - Pick walkable tiles spread across the map (grid-with-jitter, Poisson-ish, or seeded-random with a
     **min spacing** so they don't clump), **skipping blocked tiles**.
   - Assign each placed node a **type from the registry** (Tree/Rock/Plant) — distributed/round-robin or
     seeded-random; a rough even mix is fine.
2. **Count / density — configurable, moderate default.** Scale to map size (e.g. a target density like
   ~1 node per `K`×`K` tiles, or a count derived from walkable area) via `ServerOptions`
   (`MMO_RESOURCE_NODE_COUNT` or a density var) with a sensible default. **Pick a moderate default** so a
   moving player usually has a few nodes in view without flooding the world with entities — and document
   the number you chose. (More nodes = more entities = more AOI-scan work; see note.)
3. **Respawn must not scan every node each tick.** Today `RespawnResourceNodes` iterates **all**
   `_resourceNodeEntities` every tick. With many scattered nodes that's an O(total)/tick cost that grows
   with the world. Track **depleted** nodes only (a queue/heap keyed by `respawnAtTick`, or a small
   dirty-set) so per-tick respawn work is **O(depleted)**, not O(total). Available nodes cost nothing.

## Files (server only)
- `src/Mmo.Server/Runtime/Zone.cs` — world-wide deterministic scatter (replace `PlanResourceNodeScatter`).
- `src/Mmo.Server/Runtime/GameServer.cs` — `ScatterResourceNodes` wiring; the depleted-only respawn tracking.
- `src/Mmo.Server/Configuration/ServerOptions.cs` — node count/density config + default.
- Remove the leftover **"Ancient Marker" placeholder** `SpawnTransient(EntityKind.Resource, …)` while here —
  it's a Resource-kind entity with no `ResourceNode` (dead Stage-3 scaffolding) that would render as a fake
  node. (If removing it touches unrelated tests, surface it.)

## Tests
- Scatter spans the map (min/max X,Y reach well beyond any single cluster; not confined near `_spawnTiles[0]`),
  all placed tiles walkable, count ≈ the configured target, deterministic (same seed → identical layout).
- Respawn: a depleted node returns to Available after `respawnTicks`, and the per-tick respawn path does
  **not** iterate available nodes (assert via the tracking structure, not a wall-clock).
- Existing harvest/AOI/interact integration tests still pass.

## Acceptance
- Nodes are spread across the walkable map; a player anywhere walkable finds nodes within a short walk
  (verify via the client / `client_entities` — Resource entities appear as you move, not just at one spot).
- Deterministic across restarts; placement skips blocked tiles; type mix present.
- `run-checks.cmd` green. A 120/30s stress on 1000² with the scattered nodes shows the per-tick budget
  (movement/other/AOI) still healthy and **gc 0** (watch that the node entities don't bloat the AOI scan —
  if they do, that's more fuel for S41 grid-AOI, note it). Do NOT commit — Orchestrator reviews.

## Notes
- No client/protocol change — nodes already replicate by AOI and render via S39.
- Scattering many node entities increases total world-entity count, which the **naive AOI scan** (S40's
  growing cost) walks → this and **S41 (grid AOI)** reinforce each other; keep the default count moderate
  until S41 lands.
- This makes the gather loop playable for a returning character (no DB reset needed).
