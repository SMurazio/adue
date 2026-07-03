# Town + Floor 1 Blockout — Design (PROPOSED, orchestrator, 2026-07-03)

**Status: PROPOSED — awaiting user sign-off on §2; layout itself (§4) is expected to iterate by feel.**
First authored-map work (game-direction §2 tower structure + §4 floor/strata arc). Goal: a playable
GRAYBOX of the base town and the first tower floor — colored planes and existing props, no art pass —
so town scale, floor-1 width, and the walk between them can be *felt*, and the ecology v1 starter
regions (docs/ecology-v1-design.md §7) get real geography.

## 1. Ground truth (verified 2026-07-03)

- The map is ONLY a blocked-tile set; no tile metadata exists. Client REGENERATES it from ZoneInfo's
  (seed, genVersion) via shared `TerrainGenerator` and hard-fails on ContentHash mismatch — authored
  layout MUST live in shared code (`TerrainGenerator.cs:15-17`, `ZoneModel.cs:13-24`).
- genVersion 1 = fixed layout (border + 3 wall segments), seed unused. New layouts are sanctioned via a
  NEW genVersion.
- Client floor visuals come from a client-only bitmap (`terrain.png` → grass/terrain cells) that is NOT
  reconciled with collision — two sources of truth today. Floor = unshaded colored quads in 32-tile
  MultiMesh chunks (`TerrainPainter.cs`); walls = gray boxes on blocked tiles. Adding flat-color
  categories is trivial.
- Props exist NOW via DisplayName→archetype (`EntityVisualFactory.cs:46-56`: "House" → casa_magica
  sprite, "Portal" → portalemagico.glb; Rock/Tree meshes) on Resource/transient entities at authored
  tiles.
- One zone, no transfer mechanic (only the seams). Spawn = distribution grid around map CENTER (not
  authored). No PvP exists at all (players are never damageable), so "safe town" needs no combat flag.

## 2. Decisions

**D1. One ASCII grid in Mmo.Shared is the single source of truth.** New `AuthoredMaps.TownAndFloor1`
(a `string[]`, one char per tile) parsed by a shared `AuthoredMap` reader into: blocked set, per-tile
SURFACE CATEGORY (byte), spawn anchor tiles, and prop markers. `TerrainGenerator` genVersion 2 returns
it; ContentHash covers it (client/server drift still hard-fails). WHY: shared code is the only place
both sides can see (no wire payload today); ASCII is diffable, hand-editable, LLM-editable, and — the
big win — collision and visuals can never disagree again because they are the same characters.

**D2. One zone; town and floor 1 are areas of one 192×192 map.** (User 2026-07-03: size is free —
"bigger if needed". 192×192 buys a genuinely WIDE floor 1 and real walking distance to the Verge; the
engine is indifferent: blocked-set memory is trivial, AOI is 18u so per-client traffic is size-blind,
wire caps at 32767.) No zone transfer in this arc. The tower fantasy is carried by geography (walls +
one gate). WHY: transfer tech is real netcode work that would gate a graybox whose entire purpose is
feel; floors-as-zones arrives with floor 2+, and an authored map relocates into its own zone as data.

**D3. Char alphabet (the authoring contract):** `#` wall (blocked) · `.` grass · `,` dirt/road ·
`:` town cobble · `-` dungeon stone · `~` water (blocked, blue — visual variety, no swim tech) ·
`S` town spawn anchor (walkable cobble) · `H` house prop · `P` portal prop · `T` tree / `R` rock
(resource-node pin) · space = out-of-world (blocked). Unknown char = parse ERROR (fail at boot + a
test, not silently). Categories map to flat albedo colors client-side (one material each, unshaded,
existing MultiMesh path).

**D4. Spawn is authored.** New players + invalid persisted positions spawn round-robin on the `S`
tiles (town plaza). The center-grid `SpawnDistribution` machinery stays for stress/dev via env override.
WHY: "cozy base, hostile heights" starts with waking up in town, and 30 concurrents in a small plaza
reads as a crowd (perception-of-small).

**D5. Town safety = ecology, not code.** No monster region overlaps the town; aggro/leash tuning keeps
wanderers out. No sanctuary flag, no combat change. WHY: zero new combat tech for the graybox; a real
sanctuary rule belongs with PvP decisions (currently nonexistent by design).

**D6. Resource scatter avoids authored surfaces.** The existing deterministic scatter only places nodes
on grass (`.`), never on cobble/stone/road; `T`/`R` chars additionally PIN specific nodes (town's
oak, the quarry rock). WHY: keeps gather content without junk inside buildings/dungeon.

**D7. Ecology regions re-anchor to the floor-1 wings.** ecology.json's three starter regions become the
three wings (§4): Slime Hollow (west), Gnoll Scrubland (east), The Verge (north). The ecology doc's
region RECTS are authored to match this map. WHY: one geography, two systems, mutual legibility.

## 3. What this is NOT

No art pass (colored planes + the two existing sprites/meshes only), no interiors (houses are solid
props), no doors/gates that open, no NPCs, no zone transfer, no new entity kinds, no water/swim
mechanics, no minimap art (the minimap already renders walls/entities; region shading arrives with
ecology E4).

## 4. Layout brief (the map M3 authors; iterate by feel)

192×192. South-center: TOWN (~30×24) — plaza with 4-6 `S` tiles, 6-8 `H` houses on a `:` cobble grid,
`,` road ring, one pinned oak + rock. One `,` road north through a 3-tile GATE in a full east-west
wall — the tower threshold (a `P` portal prop flanks it as the future floor-2 stub). North of the wall:
FLOOR 1, deliberately WIDE (game-direction: low floors split the crowd by geography): three wings
separated by rock fingers — WEST pocket arenas (slime hollow; open rooms ≥8u across for telegraph
dodging), EAST scrubland (gnoll skirmish lanes, 4-6u corridors + cover rocks), FAR-NORTH the Verge
(remote, overgrowth-prone). Wing mouths ≥5u wide (no chokepoint camping); dead ends only in the Verge.
`~` pond west of town for visual anchor. Every walkable area reachable from the plaza (M1 parser test:
flood-fill from S covers all walkable tiles — no orphan pockets, ever).

## 5. Tasks (each = one todo + one commit; lower-model sized)

- **M1 — AuthoredMap substrate (shared):** parser (chars → blocked/category/markers, unknown-char
  error), `TerrainGenerator` genVersion 2 path + ContentHash coverage, flood-fill reachability +
  determinism + round-trip tests. A 12×12 TEST map, not the real one.
- **M2 — client painter categories:** `TerrainPainter` consumes AuthoredMap categories (genVersion 2)
  instead of terrain.png (which stays for genVersion 1); category→color material table; walls unchanged.
  Headless test on the category→material mapping.
- **M3 — the map content:** the real 192×192 ASCII grid per §4 + authored spawn (D4) + prop spawning at
  boot from markers (existing archetype hook) + scatter-on-grass-only (D6) + ecology.json rect update
  (D7). Server boots genVersion 2 by default; genVersion 1 remains reachable (env) for old tests.
- **M4 — feel-test (human):** walk spawn→plaza→gate→each wing. Verdicts: town reads cozy-small? floor 1
  reads WIDE? gate reads like a threshold? arena pockets fit telegraph dodging? Iterate §4 in-place.

Order: M1 → M2 → M3 → M4. Full-rigor review on M1 (shared codegen + hash contract — a drift bug is a
client hard-fail); M2/M3 standard.
