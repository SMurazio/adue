# Phase 0 — Position-Type Seam (implementation spec)

Part of the continuous migration (`docs/continuous-migration-roadmap.md`, tracker
`docs/continuous-migration-progress.md`). Phase 0 = the **type seam ONLY**, behavior-frozen.

## The contract (what "done" means)
Entity POSITION becomes a continuous float (`WorldVector`), but movement stays **byte-for-byte tile-stepped**.
Positions only ever hold **integer tile-center values** in Phase 0. `Velocity` is added but always `Zero`.
`SpeedUnitsPerSecond` is added but **read by nothing**. The wire (v35), persistence, `Direction8`, the tile MAP
grid, and ALL client code stay tile-typed. **The branch compiles and 100% of existing tests pass with at most
*accessor-rename* edits** — if a test's *expected value* changes, that is a Phase 0 BUG (see R5). That green,
behavior-identical checkpoint is the whole deliverable: it proves the retype was mechanically faithful and gives
Phase 1 a known-good base to make movement actually continuous.

## Why Phase 0 ≠ Phase 1
The retype is a big-bang compile event (the tree won't build until all ~243 `.Tile` sites convert). Bundling the
behavior change (integrator, deleting commit-step, predictor port) into a non-compiling tree means a regression
can't be bisected to "retype bug" vs "integrator bug." Phase 0 stays behavior-frozen so every existing tile-parity
test still passes unchanged — that's the safety net. Phase 1 is then a pure behavioral diff against a green seam.

## The position type — create `src/Mmo.Shared/Domain/WorldVector.cs`
`public readonly record struct WorldVector(double X, double Y)` in `Mmo.Shared.Domain` (Shared, so server + wire +
client all see it). **`double` not `float`** (the proven `ContinuousMover`/`ContinuousCollision`/`ContinuousPredictor`
are double-based; float would break Phase 4 reconcile determinism — document this in the XML comment). **`X,Y` not
`X,Z`** (the game is 2D top-down; the Phase 1 port maps the experiment's `Z`→`Y`).
API: `Zero`; `+`/`Add`, `-`/`Subtract`, `*`/`Scale`(scalar); `Length`, `LengthSquared`, `Dot`, `Normalized()`;
bridges `static FromTile(TileCoord)`, `FromTile(int,int)`, `ToTileRounded()`, `ToTileFloored()`. The bridges are
what make the boundary cheap.

## The tile/continuous boundary
- **BECOMES `WorldVector`:** `WorldEntity.Tile` → `WorldEntity.Position` (+ `Velocity`).
- **STAYS `TileCoord`:** the MAP (`TileGrid`/blocked set/`TerrainGenerator`), spawn authoring (`MonsterSpawner.Tile`
  leash home), `Direction8`+`Delta()`, the wire (`EntityStateSnapshot.Tile`, codec, v35), persistence
  (`tile_x/tile_y`), and **all client code** (decodes `TileCoord` off the unchanged wire).
- **Exactly two conversion points, both server-side, both lossless in Phase 0** (positions are exact tile centers):
  (1) grid/walkability: `Position → ToTileRounded() → TileGrid.IsWalkable`; (2) wire build: `Position → TileCoord →
  codec short`.
- Add a derived `public TileCoord TileCoord => Position.ToTileRounded();` on `WorldEntity` so the many tile-needing
  read sites get a tile without each converting.

## Speed stat (dormant in Phase 0)
Add `WorldEntity.SpeedUnitsPerSecond` (double) + a `ServerTuning.BaseMoveSpeedUnitsPerSecond` knob (register in
`ServerTuningRegistry`), derived `SpeedUnitsPerSecond = BaseMoveSpeedUnitsPerSecond × SpeedMultiplier`
(base = `1000.0 / StepCooldownMs`, reproducing today's cadence). **Keep `SpeedMultiplier` +
`EffectiveStepCooldownTicks` exactly as-is — they still drive Phase 0 movement.** The units/sec stat is stored but
read by nothing until Phase 1's integrator switches onto it and deletes the cooldown/commit machinery.

## Compile order (leaf-type-first, bottom-up)
- **A (additive, compiles alone):** create `WorldVector` + bridges. No callers yet.
- **B (the spine — the ONE real-decision file):** `WorldEntity.cs` — `Tile`→`Position` (tile-center valued), add
  `Velocity`(Zero)/`SpeedUnitsPerSecond`, the derived `TileCoord` accessor; `TryStep`/`TryCommitStep*`/`TeleportTo`
  compute the integer target tile (unchanged math) then assign `Position = WorldVector.FromTile(target)`.
- **C–E (mechanical server reads):** `Zone`, `SpatialEntityGrid` (**key on `entity.TileCoord` — keep integer cell
  math, defer float to Phase 6**), `GameServer` (AOI/distance/snapshot-build read `.TileCoord`, **keep integer
  distance math for parity**), `MonsterRoamAi`/`MonsterSpawner`, `MeleeConeResolver`/`FreeAimSectorResolver`,
  traces, `WorldState`/persistence (read `.TileCoord`; load builds `FromTile`). Wire types **unchanged**.
- **F (client):** **untouched** — decodes `TileCoord` off the unchanged wire (ported in Phases 4/5).
- **G (tests):** mechanical `entity.Tile` → `entity.TileCoord` accessor renames ONLY.

## Sub-commits (each compiles + green, except 2 is the irreducible big-bang)
1. `feat(shared): add WorldVector position type + TileCoord bridges` — Stage A, additive.
2. `refactor: retype WorldEntity.Tile → WorldVector Position (tile-center, behavior-frozen)` — Stages B–G as ONE
   atomic commit (a partial retype doesn't compile; no smaller revertable unit). Velocity dormant, derived
   `TileCoord` accessor, all tests pass via accessor renames.
3. `feat(server): introduce dormant SpeedUnitsPerSecond stat + BaseMoveSpeedUnitsPerSecond tuning`.
4. `test: WorldVector unit tests` (Add/Scale/Normalized/Length/FromTile/ToTileRounded round-trips incl. tile-center
   identity).

## Risks
- **R5 (the watched one): test churn hiding a real regression.** Phase 0 mass-edits test assertions; a genuine
  behavior change could be silently "fixed" by editing the expectation. RULE: **accessor rename only** — if a
  test's *expected value* changes, STOP, it's a Phase 0 bug. The independent reviewer checks the diff for exactly this.
- R1 rounding off-by-one — lossless in Phase 0 (exact tile centers); becomes real in Phase 1 (floor-vs-round). Assert
  the round-trip.
- R2 a `.Tile` *write* mechanically converted as a read — write sites are only inside `WorldEntity` (`private set`);
  contained.
- R3 AOI/`SpatialEntityGrid` parity drift if someone floats the distance math — DON'T; keep integer `TileCoord` math
  (Phase 6 floats it). The AOI parity suites are the guard.
- R4 `double`-not-`float` — locked; don't let a reviewer "optimize" to float (breaks Phase 4 determinism).
- R6 scope creep into Phase 1 — hold the line; Phase 0 ends behavior-frozen + green.
