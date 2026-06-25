# Phase 2 — Continuous Collision, PLAYERS (implementation spec)

Part of the continuous migration. Base: Phases 0+1 (players integrate continuously via `WorldEntity.IntegrateMovement`
/ `Zone.IntegrateMovement`, NO collision — walk through walls). Scope: player movement collides with walls
(swept-circle + wall-slide + anti-tunneling), walls DERIVED from the tile map's blocked-tile set. Monsters tile-step
(Phase 8). Wire v35 (Phase 3). Server-side; code placed so Phase 4's client predictor reuses it byte-identically.

## Key findings (de-risked)
- **Collision code → `Mmo.Shared.Domain`** (not Client.Core like the spike). Both server (now) and the Phase-4 client
  predictor must call the BYTE-IDENTICAL deterministic resolver — that's the determinism contract.
- **The client already holds the blocked-tile set:** `ZoneModel` regenerates the identical `HashSet<TileCoord>` from
  `(Width,Height,Seed,GenVersion)` via the same shared `TerrainGenerator`, content-hash-verified. So Phase 4 derives
  the SAME walls from the SAME set via the SAME shared function — **no new wire payload for the map.**
- **Tile geometry:** a tile centre is integer `(X,Y)`; blocked tile `(tx,ty)` → AABB `[tx-0.5,ty-0.5 .. tx+0.5,ty+0.5]`
  (1×1, tile pitch 1). **Body radius 0.5** inscribes a 1×1 body (matches the spike).
- **Z→Y rename** throughout the port (the established convention: spike's X/Z ground plane → game's X/Y).
- **Integrator seam is isolated:** player movement funnels through `GameServer.IntegrateHeldMovementIntents` →
  `Zone.IntegrateMovement` → `WorldEntity.IntegrateMovement` (raw `Position += Velocity·dt`). Monsters use the disjoint
  `TryStep` path, so players-only is automatic.

## The resolver port (`src/Mmo.Shared/Domain/ContinuousCollision.cs`)
Direct port of `exp:ContinuousCollision` with Z→Y (`Wall(MinX,MinY,MaxX,MaxY)`, `deltaY`, `penDown/penUp` on Y).
**Keep all `double`, branch-identical, no float/SIMD/RNG/clock**; preserve `SubStepMaxFraction=0.5`, `ResolvePasses=2`,
the epsilons, and the deterministic tie-break order (X axis wins) VERBATIM — that ordering is part of the byte-identical
contract. Keep the primitive `Resolve(startX,startY,deltaX,deltaY,radius,Wall[])` hot path; add a thin `WorldVector`
overload for call-site readability. Port `Wall.FromCenter`. Drop the experiment's hardcoded `BuildWalls()`/arena.

## AABB derivation + spatial query
**Per-tile AABBs + a per-tick swept-neighborhood query** (reject greedy-merge — an extra deterministic algo to keep
bit-identical on both sides; the query already bounds wall count). 
- Pure tile→AABB mapping in **`Mmo.Shared.Domain` (`TileWalls`)**: `ForTile((tx,ty)) → Wall.FromCenter(tx,ty,0.5,0.5)`
  + `NeighborhoodWalls(blockedSet, sweptBox)`. Phase 4's client calls the EXACT same function — the determinism linchpin.
- The per-tick query on the server's `TileGrid`/`Zone` (owns `_blockedTiles`): compute the swept AABB of the body
  (start+end, each expanded by radius), floor/ceil to a tile box (~2×2–3×3 at sub-tile per-tick deltas), emit one
  `Wall` per blocked tile inside, in **stable row-major order** (matches `TerrainGenerator`), into a **reused scratch
  buffer** (zero per-tick alloc; single-threaded tick). Must be a deterministic SUPERSET of the swept+radius region.

## Body radius
Constant `0.5` for Phase 2, but a tuning knob: `ServerTuning.BodyRadiusUnits` (default 0.5, **clamp strictly < 0.5** so a
1-tile-wide gap stays passable) + register the key. Add a shared `CollisionDefaults.BodyRadius = 0.5` that both the
server default and the future client read — so the common path needs no wire field (the tuning override is server-feel
only until Phase 3 decides whether to replicate it). **R-radius/dt: Phase 4 must use the IDENTICAL radius + dt.**

## Wiring
- `WorldEntity`: keep it grid-agnostic — add `ApplyResolvedMove(WorldVector newPosition)` that applies the collided
  end position + the existing tile-crossing bookkeeping (`StateRevision`/`StepSequence` bump on rounded-tile change,
  facing, stop). The caller computes the collided move.
- `Zone.IntegrateMovement`: `delta = velocity·dt` → `TileGrid.QueryNearbyWalls(pos, delta, radius, scratch)` →
  `ContinuousCollision.Resolve(pos, delta, radius, scratch)` → `entity.ApplyResolvedMove(resolved)` → spatial migrate
  on tile crossing (unchanged).
- `GameServer.IntegrateHeldMovementIntents`: threads `radius` from `_tuning` (it already has dt/unitDir/gates).
- **Determinism:** same start + delta (unitDir×speed×dt) + radius + wall set (same query box → same blocked tiles →
  same `Wall[]` in the same order) → byte-identical `Resolve`. Pin fixed `dt = 1/TickRate` (Phase-1 R4).

## Tests
- `tests/Mmo.Shared.Tests/ContinuousCollisionTests.cs` (port, Z→Y): stop-at-surface, angled slide (tangential kept /
  normal removed), fast-move no-tunnel, open move unaffected, **byte-identical determinism** (`DoubleToInt64Bits`).
- `tests/Mmo.Shared.Tests/TileWallsTests.cs` (new): tile→AABB exact corners; neighborhood query is a stable-order
  SUPERSET; determinism (same set+box → identical `Wall[]`).
- `tests/Mmo.Server.Tests/`: the INVERSE of Phase-1's walk-through — a player integrating into a blocked tile STOPS at
  the surface (re-enable the assertion Phase 1 deferred); glancing → slides; open → unchanged (regression); server-layer
  determinism (same start+dir+map → identical Position); monsters still block via `IsStepWalkable` (regression).

## Sub-commits (1–4 additive + independently revertable; 5 = the behavioural flip; keep separate)
1. `feat(shared): port ContinuousCollision (swept-circle + slide + anti-tunnel) to Mmo.Shared` (+ ported tests).
2. `feat(shared): TileWalls — per-tile AABBs + neighborhood query` (+ tests).
3. `feat(server): TileGrid/Zone nearby-walls query (scratch buffer, row-major)`.
4. `feat(server): BodyRadius tuning knob + shared CollisionDefaults`.
5. `feat(server): collide player continuous movement against walls` — the flip (`ApplyResolvedMove` + Zone query+resolve
   + GameServer radius); fold in the Server wall-block/slide/determinism tests. The revert point for collision feel.
6. `docs: Phase 2 progress`.

## Risks
- **R-determinism (HIGHEST — gates Phase 4):** any divergence (server-only `HashSet` order, a float cast, unstable wall
  order) desyncs prediction at every wall. Mitigation: one shared derivation+resolver, order-stable row-major, all-double
  branch-identical, fixed dt, byte-identity test. Do NOT let server-only convenience leak into the resolved path.
- **R-wall-query perf:** bounded by the swept-neighborhood box + zero-alloc scratch; measure in the Phase-12 stress gate.
  Greedy-merge deferred (determinism-safe only).
- **R-feel (roadmap's #1 schedule risk):** snag on corners, sticky/slippery slide, tunnel at speed — validated in the
  spike's open arena but NOT against real derived tile geometry. Budget a manual feel-iteration arc; `BodyRadius` +
  `SubStepMaxFraction` are the knobs; watch corner behavior (the continuous analog of the deleted S75 corner-cut).
- **R-gap-width:** radius < 0.5 so 1-wide corridors stay passable.
- **R-monster-divergence:** monsters strictly on `TryStep` (Velocity stays Zero); only the player path gains collision.
