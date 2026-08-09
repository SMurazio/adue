# S — Reframe commit 1: author the base-camp map (genVersion 3), coexisting with v2

Part 1 of the base-camp reframe (`docs/duo-base-camp-reframe.md`, Fable-scoped). Author a NEW small
map and re-home the sealed pockets onto it — WITHOUT flipping the live world yet (that's commit 2), so
the full suite stays green and proves coexistence. SHARED + TESTS only in this commit.

## Build
1. **`AuthoredMaps.BaseCamp`** (new stamp) — a small map (~48×48; big enough for a ~16×16 camp island +
   the two 24×24 sealed pockets + margins). The camp island is a NON-GRASS surface (cobble/dirt/
   DungeonStone) so it masks the node scatter out for free (the BossArena trick). Clustered spawn
   anchors (`S`) so the pair lands a few tiles apart facing each other. Mirror the existing
   `BuildTownAndFloor1` stamp style (Border walls, FillRect floors, spawn markers).
2. **genVersion 3 seam** — add a v3 constant (mirror `AuthoredGenVersion`) and a generator branch that
   emits `BaseCamp` for v3. DO NOT change `CurrentGenVersion` yet (world stays v2 — commit 2 flips it).
   `TownAndFloor1` + its stamp + its whole test file stay UNTOUCHED.
3. **Re-home the pockets** — move `BossArena` and `PracticeRoom` exterior coords so both sealed pockets
   fit inside the new BaseCamp dims and are stamped by the BaseCamp program. **KEEP THE 24×24 INTERIOR
   SHAPE IDENTICAL** — do NOT touch the arena's interior geometry, entry-tile spacing, BossSpawnTile,
   or CoreRootTile *relative* layout (the tether/beam sweet-band depends on interior-relative positions;
   the gated combat must not move). Entry/spawn/core tiles derive from the new exterior origin.
   - Because BossArena/PracticeRoom are stamped into BOTH maps' programs today, decide + state how they
     stamp on v2 vs v3 (they can keep stamping on v2's TownAndFloor1 too, OR only on BaseCamp — surface
     as a FORK; the pockets must remain sealed + carved from reachability on whichever map is live).

## Tests
- New `BaseCampMapTests`: dims; the camp island is walkable + non-grass; clustered spawn anchors exist;
  BOTH sealed pockets are carved out of the reachability invariant; flood-fill-from-every-S reaches all
  non-pocket walkable tiles; a fresh ContentHash pin (STALE literal + a `REPIN` note — the orchestrator
  fills it from the gate, per the M3-F1 process).
- New NodeCatalog CatalogHash pin for the BaseCamp map (all-non-grass camp ⇒ empty catalog) — STALE +
  REPIN note.
- Existing `TownAndFloor1MapTests` / `NodeCatalogTests` / reachability tests stay GREEN (v2 unchanged).

## Guardrails
- NO wire/protocol change expected. If re-homing the pockets forces one, STOP and surface it.
- Do NOT flip `CurrentGenVersion`, do NOT touch ecology/spawner configs, do NOT touch the arena INTERIOR
  geometry, do NOT delete or edit `TownAndFloor1` or its tests. Those are commit 2 / never.
- Content-hash literals: leave STALE with clear `ADUE REFRAME REPIN` notes; the orchestrator runs the
  gate and pastes the computed values (never guess).

## Acceptance
- `run-checks` green with the v2 world still live (coexistence proven); `BaseCampMapTests` pass (after
  the orchestrator fills the hash pins); arena interior geometry byte-identical. Delete this file in the
  landing commit. Commit 2 (flip) + commit 3 (portal/flow) follow as their own todos.
