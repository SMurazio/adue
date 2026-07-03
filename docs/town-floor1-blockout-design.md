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

**D2. One zone; town and floor 1 are areas of one 384×384 map** (orchestrator's call — user delegated
size/layout 2026-07-03 after 192 "still feels small"). Sizing by TILE-TIME, not tiles: town→gate ≈
30-45 s, wing hearts ≈ 1-2 min, the Verge ≈ 2.5-3 min of committed walking — expedition distance for
the 45-min session shape, while staying dense enough that a handful of players still cross paths on
the main road. At the live zoom (~15u) that is ~25 screens across. Engine is indifferent (AOI 18u ⇒
traffic is size-blind; blocked-set memory trivial; wire caps 32767; client floor = ~147k culled
MultiMesh quads in 144 chunks — M2 must sanity-check the minimap at this size). No zone transfer in
this arc. The tower fantasy is carried by geography (walls + one gate). WHY one zone: transfer tech is
real netcode work that would gate a graybox whose entire purpose is feel; floors-as-zones arrives with
floor 2+, and an authored map relocates into its own zone as data.

**D2a. The real map is authored as STAMPS that compile to the ASCII grid.** Hand-writing 384 raw
120-char-plus lines is where character-level authoring stops being reliable (for humans and models
both). The authored artifact in shared code becomes a small ordered stamp program — fill/rect/border/
corridor/marker operations with surface categories — deterministically EXPANDED (shared code) into the
same `string[]` the M1 parser consumes; ContentHash covers the EXPANDED grid, so the drift guard is
unchanged. ASCII remains the truth format for small test maps, and AuthoredMap gains a dump-to-ASCII
so any stamped map can be eyeballed/diffed/round-trip-tested. WHY: layout iteration becomes "widen the
west arena by 4" — one number in one stamp — instead of surgery across 40 text rows; M1's parser and
tests are untouched (stamps → ASCII → parser).

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

384×384. South-center: TOWN (~44×36) — plaza with 4-6 `S` tiles, 6-8 `H` houses on a `:` cobble grid,
`,` road ring, one pinned oak + rock. One `,` road north (~50 tiles, with a meadow shoulder — the
safe-ish approach where new players watch the wall grow on the horizon) through a 4-tile GATE in a
full east-west wall — the tower threshold (a `P` portal prop flanks it as the future floor-2 stub).
North of the wall: FLOOR 1, deliberately WIDE (game-direction: low floors split the crowd by
geography): three wings off a broad gate commons, separated by rock fingers — WEST pocket arenas
(slime hollow; chained open rooms 10-16u across for telegraph dodging), EAST scrubland (gnoll skirmish
lanes, 5-8u corridors + cover rocks), FAR-NORTH the Verge (remote — a further ~90 tiles past the
commons through a narrowing pass; overgrowth-prone). Wing mouths ≥6u wide (no chokepoint camping);
dead ends only in the Verge. `~` pond west of town, a second tarn in the Verge. Every walkable area
reachable from the plaza (M1 parser test: flood-fill from S covers all walkable tiles — no orphan
pockets, ever).

## 5. Tasks (each = one todo + one commit; lower-model sized)

- **M1 — AuthoredMap substrate (shared):** parser (chars → blocked/category/markers, unknown-char
  error), `TerrainGenerator` genVersion 2 path + ContentHash coverage, flood-fill reachability +
  determinism + round-trip tests. A 12×12 TEST map, not the real one.
- **M2 — client painter categories:** `TerrainPainter` consumes AuthoredMap categories (genVersion 2)
  instead of terrain.png (which stays for genVersion 1); category→color material table; walls unchanged.
  Headless test on the category→material mapping.
- **M3 — the map content:** the stamp expander (D2a: ops → string[], deterministic, shared) + the real
  384×384 stamped map per §4 + authored spawn (D4) + prop spawning at boot from markers (existing
  archetype hook) + scatter-on-grass-only (D6) + ecology.json rect update (D7). Server boots
  genVersion 2 by default; genVersion 1 remains reachable (env) for old tests. Tests: expansion
  determinism, dump-to-ASCII round-trip, and the M1 flood-fill invariant on the REAL map.
- **M4 — feel-test (human):** walk spawn→plaza→gate→each wing. Verdicts: town reads cozy-small? floor 1
  reads WIDE? gate reads like a threshold? arena pockets fit telegraph dodging? Iterate §4 in-place.

Order: M1 → M2 → M3 → M4. Full-rigor review on M1 (shared codegen + hash contract — a drift bug is a
client hard-fail); M2/M3 standard.
