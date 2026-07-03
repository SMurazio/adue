# Procedural World Population — Design (PROPOSED, orchestrator, 2026-07-03)

**Status: PROPOSED — awaiting user sign-off on §2.** How the 384×384 authored world (and every future
floor) gets *filled* — vegetation, rocks, gatherables, and monster spawn geography — procedurally,
deterministically, and without hand-placing 147k tiles. Companion to docs/town-floor1-blockout-design.md
(authored STRUCTURE) and docs/ecology-v1-design.md (monster POPULATIONS): stamps author the skeleton,
this system grows the flesh, ecology animates it.

## 1. Ground truth this builds on

- Resource scatter exists and is the seed of this system: `Zone.PlanResourceNodeScatter` — rejection
  sampling on walkable tiles, min-spacing + clear-approach rules, SplitMix64 from `Seed ^ const`,
  deterministic per boot, grass-only + marker-excluded since M3. Uniform density everywhere (one
  `TilesPerNode` knob) — no notion of place.
- The authored map gives every tile a SurfaceCategory + gives structure (roads, town, wings) queryable
  positions. The client already renders 147k floor quads via chunked MultiMesh with bulk upload (M2).
- Live ENTITIES are the expensive thing (AOI gather, replication, snapshot bytes; ~188 nodes today).
  Anything dense must not be an entity.
- Ecology v1 (designed, not built) wants per-region `spawnTiles[]` — currently to be hand-authored.
- Determinism contract: client regenerates the world from (seed, genVersion) + ContentHash; anything
  BOTH sides must agree on lives in shared code under that discipline.

## 2. Decisions

**D1. Three layers with different truth requirements — never mix them.**
- **L1 DECOR (client-only, zero entities, zero wire):** grass tufts, flowers, pebbles, fallen leaves,
  wall moss. Pure presentation derived deterministically from (zone seed, authored categories) in
  CLIENT code — the server never knows it exists. No collision, no interaction, no hash coverage
  needed (it cannot desync anything that matters; two clients seeing identical decor falls out of the
  shared derivation anyway). Budget: tens of thousands of instances via the same chunked-MultiMesh +
  bulk-upload path the floor uses. This is 90% of perceived "aliveness" at ~0 server cost.
- **L2 INTERACTIVE NODES (server entities, replicated):** the existing trees/rocks/plants. Same entity
  path as today; this design only changes WHERE they land (D2/D3). Counts stay the same order as today.
- **L3 MONSTER GEOGRAPHY:** monsters themselves are ecology E-arc business (stock model, maxLive). This
  system contributes only the *derivation of spawn tiles* inside ecology regions (D5) — replacing
  hand-authored spawnTiles[] so every future floor gets spawn geography for free.

**D2. Placement = DENSITY FIELDS, not uniform rolls.** Per asset class, density(tile) =
`base(category) × distanceCurve(distanceToRoad/Town) × patchNoise(seed, tile)`:
- `base(category)`: grass carries tufts/flowers; dirt carries pebbles; cobble/stone carry nothing (or
  rare cracks); water nothing. The authored map is the biome map — no new biome concept.
- `distanceCurve`: computed ONCE at boot per zone by BFS distance transform from road/cobble tiles
  (integer tile distances, trivially cheap at 147k tiles). Civilization suppresses wilderness: decor
  and nodes thin near roads/town, thicken far away. The Verge reads overgrown BEFORE ecology exists.
- `patchNoise`: value noise from the zone seed — thickets and clearings instead of uniform sprinkle.
  (Deliberately value-noise-over-grid, not Perlin: 20 lines, no dependency, indistinguishable at tile
  granularity.)

**D3. Sampling = the existing rejection sampler, weighted.** Keep `PlanResourceNodeScatter`'s proven
shape (SplitMix64, min-spacing, clear-approach) and add: accept a candidate tile with probability
density(tile). No Poisson-disk machinery — min-spacing already gives blue-noise-ish distribution; the
density field does the rest. One shared `WeightedScatter` helper used by L1 (client), L2 (server), and
D5 — same math, different seeds/classes.

**D4. Authoring surface = one data table per asset class.** `Content/population.json` (server, for L2)
+ a mirrored in-code table for L1 client classes: {class id, layer, category filter, minSpacing,
baseDensity, roadCurve params, noiseScale, visual key}. Follows the monsters.json registry pattern
(clamped load, code-seed fallback). NOT hash-covered for L1 (cosmetic); L2 stays boot-deterministic
exactly as today.

**D5. Ecology spawn tiles are DERIVED, not authored.** E2 changes: each region×type samples its
spawnTiles at boot via WeightedScatter (walkable, grass, region rect, min spacing ~4, away-from-road
curve). Removes the hand-authoring in ecology.json (rects stay authored — they're design intent; tiles
inside are mechanical). Every future floor inherits spawn geography from its map + one rect per region.

**D6. Explicit NOs.** No structure generation (WFC/buildings — stamps own structure, deliberately);
no runtime decor mutation (ecology may LATER drive L1 wear-states — that hook is the visual-legibility
plan in ecology D6, not this arc); no decor entities ever; no new terrain categories; no per-player
variation (everyone sees the same world — shared-world identity matters).

## 3. Perf posture

L1: target ≤30k instances at 4-6 verts each, chunked 32×32 with the M2 bulk-buffer path, camera-culled;
build cost rides the same <250 ms zone-build budget (measured by the existing GD.Print). L2: same
counts as today (~200 entities). Boot cost: BFS + one scatter pass ≈ single-digit ms. Nothing per-tick.

## 4. Tasks (lower-model sized; P1 shared math gets the full-rigor review)

- **P1 — WeightedScatter + density fields (shared, headless):** BFS distance transform, value noise,
  weighted rejection sampler; determinism tests (same seed → identical), distribution sanity tests
  (near-road density < far density; category filters absolute).
- **P2 — L1 client decor:** class table, per-chunk instance generation off ZoneModel categories +
  P1 fields, MultiMesh render via the M2 buffer path, zone-build timing print. Feel-test: the meadow
  west of town should read "alive" at a glance.
- **P3 — L2 scatter upgrade:** PlanResourceNodeScatter consumes P1 (density-weighted, road-suppressed);
  same node counts (tune baseDensity to today's ~188); existing determinism/spacing tests keep passing.
- **P4 — (rides ecology E2) spawn-tile derivation per D5.**

Order: P1 → P2 (the visible win) → P3 → P4-with-E2.
