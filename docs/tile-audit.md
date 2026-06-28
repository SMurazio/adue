# Tile-reference audit (continuous-migration cleanup)

Goal: the game migrated tile-stepped → CONTINUOUS movement (`WorldVector` doubles, 1 unit == 1 old tile, fractional
everywhere). Hunt down every place still THINKING IN DISCRETE TILES where it should be continuous **range / world
units**, EXCLUDING graphical + map-generation tiles (those stay tile-authored, by the user's call). Produced by 3
parallel read-only audits (server runtime / shared+protocol / client non-graphical), 2026-06-28.

## Verdict
The tile *map* layer is legitimately tile-based and KEPT (terrain gen, the blocked-tile set, `TileWalls`/`TileGrid`
collision, `TileCoord`, spawners, tile placement). The overwhelming majority of remaining "tile" references are
**TERMINOLOGY** — names/comments/labels saying "tiles" for values that are ALREADY continuous world-units (zero
behavior change to fix). Only a few are real CONVERTs.

---

## DONE
- **Monster ranges → continuous world-units** (commit `57b01f0`): `MonsterType.RoamRadius/AggroRadius/ChaseLeash`
  int→double (fractional range); `DeaggroRadius` continuous; clamps→double. **Bug fixed:** the F1 "attack range" knob
  edited the integer-tile `AttackRange` the AI NEVER read (the AI uses `AttackRangeUnits`) — repointed to
  `AttackRangeUnits` (continuous, clamp 0.5..8) and retired the dead int field. Hop tab labels dropped "(tiles)".
  No protocol change (the v40 data-driven MonsterTuning wire already carries every field as a double).

---

## TERMINOLOGY — pure renames, no behavior/wire change (READY — Commit B, awaiting greenlight)
All of these carry CONTINUOUS values; the identifier/comment/label just says "tile". Renames are compiler-checked +
test-verified. A few touch a replicated record field name (byte layout unchanged → no protocol bump; server + in-repo
client rename together).

Shared:
- `InteractionTuning.InteractionRadiusTiles` (+`...Squared`) → `...Units` (1.5 world-units; used vs `WorldVector`).
- `FreeAimSector` params `radiusTiles` / `bodyRadiusTiles` → `radiusUnits` / `bodyRadiusUnits` (+ comments).
- `CombatTuningSnapshot.RadiusTiles` → `RadiusUnits` (replicated double; ripples to server resolver + client display).
- `CollisionDefaults` comment "tile units" → "world units"; `EntityKind` comment "tile-step path" → continuous;
  `ProtocolCodec` comment "radius tiles" → "radius units".

Server:
- `ServerTuning.InterestRadius` comment "in tiles" → "world units".
- `FreeAimSectorResolver` `radiusTiles` / `gatherRadiusTiles` param names → `*Units` (values are continuous doubles).
- `MonsterRoamAi` class-header conversion-table note (historical; light touch).

Client (non-graphical):
- `CursorHeading.deadZoneTiles` → `deadZoneUnits`; `MmoClientRoot.MouseHeadingDeadZoneTiles` → `...Units`.
- `MovementSpeedOptions.tilesPerSecond` → `unitsPerSecond` (+ the "/s" dropdown label).
- `HarvestTargeting.InteractionRadiusTilesSquared` → `...UnitsSquared` (follows the shared rename).
- `MmoClientRoot.MotionSnapJumpTiles` → `...Units`; `_aimWedgeRadiusTiles` → `_aimWedgeRadiusUnits`.
- `HudState.MinimapObject.FootprintTiles` → `FootprintUnits` (+ "(tile space)" comment → "continuous world coords").
- F1 Combat tab label "radius (tiles)" → "radius (units)".

CONVERT-but-rename-only (wire field name, byte-identical → no bump):
- `ServerHelloMessage.InterestRadiusTiles` → `InterestRadiusUnits` (already a continuous float); follow with
  `ClientCore ServerInfo.InterestRadiusTiles` → `...Units` + the debug-overlay reads.

---

## KEEP (map / collision / graphical) — do NOT convert
`TileCoord`, `TileWalls.NeighborhoodWallsForMove`, `TerrainGenerator`, `TileGrid` + the blocked-tile set,
`Zone.IsWalkable(TileCoord)`, `QueryNearbyWalls`, `MonsterSpawner.Tile`, dummy/resource/spawn-tile placement,
`Direction8.Delta()`, `DefaultSpawnTile`, all map/minimap/terrain rendering. The world is authored on a tile grid and
continuous physics reads walls off it — intentional, per the user (don't touch map gen now).

---

## DECISION — working-as-intended; needs a human call before any change
- **Persistence `tile_x`/`tile_y` columns** (CharacterRepository): the float `pos_x/pos_y` are the truth; the tile
  columns are kept coherent for tile-keyed queries. Coherence pattern — leave unless we drop tile queries.
- **`SpatialEntityGrid` cell size** keyed on rounded `TileCoord`: a documented PERFORMANCE knob (correctness is
  Euclidean downstream). Not a tile-thinking bug.
- **AOI gather quantization** — `GameServer.ResolveAoiQueryRadiusTiles` / `MonsterRoamAi.GatherRadiusFor` /
  `FreeAimSectorResolver` gather radius: a continuous range is `Math.Ceiling`'d to an integer-tile coarse pre-filter
  box; the PRECISE test is Euclidean. Correct as a superset; converting buys nothing but risks an AOI miss. Leave
  (maybe just the `*Tiles` naming in the TERMINOLOGY pass).
- **Roam-destination fallback scan** (`MonsterRoamAi.TryPickRoamDestination`): the deterministic fallback scans
  integer tiles of the leash box (the disc sample is already continuous). Works; tile-framed only in the fallback.

---

## Slime feel polish — DEFERRED (after the tile cleanup, per the user)
The data-driven tab already exposes the knobs (`hopDistance`/`hopHeight`/`hopAirborneMs`, now relabeled off "tiles").
The remaining ask — make the slime *feel* right: a good RANGE, a DELAY between jumps, a max HEIGHT — is tuning the
defaults + confirming "delay between jumps" reads clearly (it's move-cadence minus airborne). Separate task:
`todo/N-slime-feel-polish.md`.
