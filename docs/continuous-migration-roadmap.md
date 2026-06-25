# Continuous-Movement Migration Roadmap

**Status:** decision-support artifact (2026-06-25). NO code changes accompany this doc — it scopes the work so the
user can decide whether to migrate the real game (`main`) from **tile-stepped** movement to **continuous (free,
off-grid float-position)** movement.

This is an honest scoping document, not a sales pitch. The genuinely hard parts — real geometric collision where
there is none today, monster AI without a grid, and a hard breaking protocol bump that hits every connected client
and the collaborator — are surfaced prominently. The headline conclusion is at the bottom; the inventory is the
body.

## Where we stand

- **`main`** is tile-stepped, server-authoritative, protocol **v35**, no client prediction other than the tile
  predictor, no collision other than per-tile walkability lookup. ~243 references to `TileCoord` / `.Tile` across
  ~40 source files — tiles are the position model, not a detail.
- **`exp/continuous-movement`** is a COMPLETE, validated feel experiment (see
  `docs/continuous-movement-experiment.md` on that branch). It proved the *netcode model*: free float-position
  movement with **per-frame** client prediction + server reconciliation (`ContinuousPredictor`), a standalone
  server that integrates **per-input-by-dt** (`Mmo.Tools.ContinuousServer`), and remote-entity velocity
  extrapolation (`RemoteContinuousEntity`). It is smooth, server-authoritative, holds under injected latency, and
  shows no drift. **What it deliberately skipped:** collision, AOI, combat, monsters/AI, harvesting, loot,
  persistence, and the real protocol. It is a movement-core proof, not a port.
- An earlier `docs/continuous-movement-spike.md` (the architect pass) independently sized the full migration at
  **XL**, dominated by collision and prediction feel. This roadmap reconfirms that and goes system-by-system.

**Key reframing the experiment gives us:** the single biggest unknown the spike flagged — *does continuous
predict/reconcile feel right at latency?* — is now **answered yes**. That retires the largest *feel* risk. It does
**not** retire the largest *engineering* risks, which were always collision, AI, and the protocol break — none of
which the experiment touched.

---

## 1. Tile-coupling inventory

Sizes: **S** < 1 day · **M** ~days · **L** ~1–2 weeks · **XL** multi-week. Sizes are for *this* codebase given the
experiment already proved the core model.

### Position model (the spine — touches everything)

| System | File(s) | How it uses tiles today | What it becomes continuous | Size |
|---|---|---|---|---|
| `TileCoord` | `src/Mmo.Shared/Domain/TileCoord.cs` | `record struct(int X, int Y)` — the position type, ~243 refs / 40 files | A float position type (`WorldVector` already exists: `record struct(float X, float Y)` with normalize/add/scale). Retype every `.Tile` site. | **XL** (mechanical but wide) |
| `WorldEntity.Tile` | `src/Mmo.Server/Runtime/WorldEntity.cs` | Authoritative integer position; mutated atomically on accepted step | `WorldVector Position` + `WorldVector Velocity`; integrated per tick | **L** |
| `Direction8` | `src/Mmo.Shared/Domain/Direction8.cs` | 8-way enum, `Delta()` → `(±1,±1)` tile offset; the *movement unit* | **Survives as a facing/animation enum** (derive from velocity heading); no longer the movement unit | **M** |
| `RenderPosition` | `src/Mmo.Client.Core/RenderPosition.cs` | `record struct(double X,Y)` + `FromTile` + `Lerp` — already continuous; tiles enter only via `FromTile` | Feed float position straight in; drop `FromTile`. **The renderer barely changes** (experiment confirmed). | **S** |

### Movement (server + client prediction)

| System | File(s) | How it uses tiles today | What it becomes continuous | Size |
|---|---|---|---|---|
| Held-intent step loop | `GameServer.StepHeldMovementIntents`, `WorldEntity.TryStep` | One tile per tick when cooldown elapsed; `_nextEligibleTick` gate; `StepSequence++` per accepted tile | Per-tick `position += velocity·Δt`; no step gate, no per-tile cadence. The experiment's integrate-per-input is the template. | **L** |
| Commit-step system | `WorldEntity.TryCommitStep` / `TryCommitStepAuthored`, `StepCommitRequest`/`StepCommitBatch` messages, `CommitAcceptFraction`, NET2/NET3 authored-tick pacing | Client-driven early-finish of a tile step with anti-speedhack floor | **Deleted wholesale.** Speed authority moves to the server-owned speed stat; no commit machinery needed. | **M** (deletion + careful removal) |
| `EffectiveStepCooldownTicks` / `MovementCadence` | `WorldEntity.cs`, `src/Mmo.Client.Core/MovementCadence.cs`, `ServerTuning.StepCooldownMs` | Tick-quantized cadence ("1 tile per N ticks") — **the thing that forces speed into coarse brackets** | Replaced by a float `speed` stat (units/sec). This is the *entire point* of the migration. | **M** |
| Tile predictor | `src/Mmo.Client.Core/LocalPlayerPredictor.cs` (943 lines), `LocalPlayerCosmetic` | Mirrors the server's tile-step loop on a tick grid; re-anchor + re-project in-flight *tile* steps (S83) | Replaced by `ContinuousPredictor` (already built + tested on `exp/`): input ring, replay un-acked, blend/snap. **Port from the experiment, not write from scratch.** | **L** |
| S75 corner-cut rule + watchdog | `WorldEntity.IsStepWalkable`, the AI corner-cut watchdog | Diagonal step legal only if both side-tiles walkable | **Superseded by swept-circle collision + wall-slide** (see Collision). | folded into Collision |
| Remote interpolation | `src/Mmo.Client.Core/TileInterpolator.cs`, `MonsterHopInterpolator.cs` | Buffered playout between confirmed *tiles*; monsters "hop" tile-to-tile | Float position-sample playout buffer + velocity extrapolation (`RemoteContinuousEntity`, built on `exp/`). The monster-hop renderer is retired. | **M** |

### Walkability / collision — **the hardest new system**

| System | File(s) | How it uses tiles today | What it becomes continuous | Size |
|---|---|---|---|---|
| Walkability grid | `src/Mmo.Server/Runtime/TileGrid.cs` (`IsWalkable(TileCoord)` over a `HashSet<TileCoord> _blockedTiles`), `Zone.cs` | **Pure binary per-tile lookup. There is NO geometric collision anywhere today** — movement is "is this integer tile blocked?" | **Real swept-circle (entity body) vs solid geometry, with wall-slide and anti-tunneling at mount speed.** The blocked-tile set becomes solid AABBs/segments; the integrator must sweep, not point-test. | **L–XL** |
| Spatial index | `src/Mmo.Server/Runtime/SpatialEntityGrid.cs` | Cell index via floored division on int tiles | Already float-ready (floored division works on fractional coords); minor retype | **S** |

This is **the** scoping headline: continuous movement needs collision the game has never had. The experiment ran
in an open field with **no collision at all**, so it gives us *zero* evidence on the part the spike called "where
the Albion feel is won or lost." Wall-slide, not snagging on corners (the continuous analog of S75), and no
tunneling at high speed are a genuine multi-day-to-weeks feel-iteration surface in their own right.

### AOI / interest management

| System | File(s) | How it uses tiles today | What it becomes continuous | Size |
|---|---|---|---|---|
| Interest test | `GameServer.IsEntityInInterest` / `DistanceSquared` / `IsTileInInterest`, `GatherInterestCandidates`, `InterestRadius` | Euclidean distance-squared on `TileCoord`; radius already a **float** tuning value; hysteresis margin in tiles | Swap `.Tile` → `.Position` in the distance math. Radius value reinterpreted as world units (unchanged number). **No wire change for the radius itself.** | **S–M** |

AOI is the cheapest major system — it is already distance-based with a float radius. Only the coordinate source
changes.

### Combat

| System | File(s) | How it uses tiles today | What it becomes continuous | Size |
|---|---|---|---|---|
| Melee cone | `src/Mmo.Shared/Domain/MeleeCone.cs`, `src/Mmo.Server/Runtime/MeleeConeResolver.cs` | Resolves a 3-tile fan from `attacker.Tile` + `Direction8`; hits = candidates whose **exact tile** is in the fan | Becomes a geometric sector test (or reuse `FreeAimSector`); candidate selection by **radius query**, not exact-tile match | **M** |
| Free-aim sector | `src/Mmo.Shared/Domain/FreeAimSector.cs`, `FreeAimSectorResolver.cs`, `AimAngle.cs` | **Already continuous geometry** — circle-wedge `IsHit` on tile-*centre* world coords; aim is a quantized angle (ushort) | Nearly a no-op: feed real float positions instead of tile centres. The continuous combat path mostly exists already. | **S** |
| Facing | `Direction8` across combat | 8-way facing drives the cone | Derive 8-way (or finer) facing from velocity/aim; keep the enum for animation | **S** (shared with movement) |

Combat is less coupled than it looks because free-aim already does continuous geometry on tile-centre coordinates.
The open design question (below) is whether melee stays a tile-fan-shaped cone or fully goes positional.

### Monster AI — **the second hardest new system**

| System | File(s) | How it uses tiles today | What it becomes continuous | Size |
|---|---|---|---|---|
| Roam/chase/leash | `src/Mmo.Server/Runtime/MonsterRoamAi.cs` (522 lines) | Picks a random tile within a **Chebyshev** leash box of home; greedy-steps via `Zone.TryStep`; chase re-reads `target.Tile`; adjacency/leash/de-aggro all Chebyshev tile distance; corner-cut watchdog | Needs **steering** (move toward a point by velocity) + at least basic **pathing/obstacle avoidance** now that there is collision to get stuck on. Leash/aggro become Euclidean radii. | **L** |
| Spawners | `src/Mmo.Server/Runtime/MonsterSpawner.cs` (`Tile` = leash home), `SpawnDistribution.cs` | Spawn/home on integer tiles | `Position` as float; scatter in continuous space | **S** |

Monster AI rides the same `TryStep` path as players today, so it inherits the whole movement rewrite *and* gains
a new problem: with real collision, a greedy stepper can wedge on geometry. The experiment's bot just wandered an
open field — it is **no evidence** that grid-free AI navigates obstacles. Expect to add simple steering/avoidance
(full navmesh pathfinding is probably out of scope for v1 but the door opens here). There is also a client-side
`TilePathfinder.cs` + `ClickMoveController`/`PathDriver` (click-to-move A\* over tiles) that would need a
continuous path representation or removal.

### Interaction / harvesting / loot

| System | File(s) | How it uses tiles today | What it becomes continuous | Size |
|---|---|---|---|---|
| Harvest targeting + interact gate | `src/Mmo.Client.Core/HarvestTargeting.cs`, `GameServer.HandleInteract` | Chebyshev ≤ 1 adjacency (3×3) on tiles | Euclidean interaction *radius* (a float threshold) | **S** |
| Corpse loot, resource nodes | `Corpse.cs`, `ResourceNode.cs`, registries | Loot transfer is pure (item stacks); position lives on the host entity | **No change** to loot logic; only the host entity's position type changes | **S** |

### Protocol — **the hard breaking bump**

| System | File(s) | How it uses tiles today | What it becomes continuous | Size |
|---|---|---|---|---|
| Wire format v35 | `src/Mmo.Shared/Protocol/ProtocolCodec.cs` (`Version = 35`), `Messages.cs`, `EntityStateSnapshot.cs` | `EntityStateSnapshot`, `EntitySpawn`, `LoginResult`, `SpawnerMarker` carry `TileCoord` as `short X,Y` (4 bytes/pos, 12 bytes/entity snapshot). Movement input is `Direction8` + commit batches. | Float or fixed-point positions on the snapshot; continuous `MoveIntent` (heading + per-input seq + dt) replacing held-intent/commit messages; drop `StepCommit*`. | **M (code) but a HARD protocol-MAJOR break** |

The protocol change is small in code but **categorical in consequence**: v35 and the continuous wire are
mutually undecodable. Every connected client and the collaborator must update in lockstep — there is no rolling
compat window. Float positions also **inflate the hot snapshot record** (4→8 bytes/position before any other
field), which the spike flagged as a named bandwidth limiter at 120–150 visible entities. **Fixed-point + delta-
vs-baseline is likely required, not optional, and must be *measured*.**

### Persistence

| System | File(s) | How it uses tiles today | What it becomes continuous | Size |
|---|---|---|---|---|
| Character position | `db/sqlite/00x_*.sql` + `db/postgres/00x_*.sql`, `SqliteCharacterRepository.cs`, `PostgresCharacterRepository.cs`, `CharacterRecord.cs`, `PersistenceWriteBehindWorker.cs` | `tile_x`/`tile_y` **integer** columns; `SaveTileAsync`; write-behind worker flushes dirty positions | Float columns (`real`) or fixed-point ints; new migration on **both** SQLite and Postgres; loader/saver retype | **S–M** |

### Tests — large suites that change

The tile model is *heavily* tested, and these suites encode the current contract. They don't just need editing —
many need **rewriting**, and that work is real:

- `LocalPlayerPredictorTests.cs`, `WorldEntityMovementTests.cs` — tile-step parity (the predictor must match the
  server tile-for-tile). Replaced by continuous predictor/reconcile tests (the experiment's
  `ContinuousPredictorTests.cs` / `RemoteContinuousEntityTests.cs` / `ContinuousMoverTests.cs` are the template,
  ~550 lines already written on `exp/`).
- `MonsterRoamAiTests.cs` (leash over 3000 ticks, corner-cut livelock), `WorldEntityCombatTests.cs`,
  `MeleeConeResolverTests.cs`, `FreeAimSectorResolverTests.cs`, `InteractHarvestIntegrationTests.cs`,
  `HarvestTargetingTests.cs`, terrain/AOI parity suites — all assert tile distances/adjacency.
- Persistence tests (`SqliteCharacterRepositoryTests.cs`, `PersistenceWriteBehindIntegrationTests.cs`).

Rewriting tests is a **multi-day** cross-cutting task, not a rounding error. Treat it as its own line item.

---

## 2. Migration order, and whether tile + continuous can coexist

### Can they coexist? **No — not at runtime.**

The position type, the wire format, collision, and persistence all change **incompatibly**. You cannot put a tile
entity and a continuous entity in the same snapshot, and the existing `MovementRenderMode` A/B toggle is a
*client-render* switch over **one server model** — it cannot A/B two *server* models. The spike reached the same
verdict. **This is a hard replacement on a protocol-major branch, with tile-stepped `main` kept frozen** (tag the
last good tile build, e.g. `tile-stepped-stable`), not an incremental flag flip.

Within the migration branch the work *is* incremental — you build it in a sane dependency order — but you do not
ship a half-migrated `main`, and there is no period where both models serve live clients.

### Phased order (dependency-ordered)

The good news the experiment buys: **Phases 0–1 and most of 4–5 are already written and validated on `exp/`** —
this is a *port-and-integrate*, not a from-scratch build, for the movement core.

| Phase | Work | Depends on | Size |
|---|---|---|---|
| **0** | Shared position type: adopt `WorldVector` as the entity position; introduce `speed` stat; retype the `~243` `.Tile` call sites mechanically (the wide-but-shallow seam edit) | — | **L** (wide) |
| **1** | Server continuous integrator: `position += velocity·Δt` per tick; delete the step gate / cooldown / commit-step system. Port `ContinuousMover`/integrate-per-input from `exp/`. | 0 | **M** (port) |
| **2** | **Continuous collision** — swept circle vs solid geometry + wall-slide + anti-tunneling. **The big unknown; the experiment skipped this entirely.** Replaces S75 + the corner-cut watchdog. | 1 | **L–XL** |
| **3** | Wire: float/fixed-point positions, continuous `MoveIntent` (heading + per-input seq + dt), drop `StepCommit*`, **protocol-major bump**. | 0,1 | **M** + hard break |
| **4** | Client continuous prediction + reconcile: port `ContinuousPredictor` (input ring, replay, blend/snap) into `MmoClient`/`ClientEntity`. | 1,3 | **L** (port + integrate) |
| **5** | Remote interpolation: float position-sample playout buffer + velocity extrapolation; retire `TileInterpolator`/`MonsterHopInterpolator`. Port `RemoteContinuousEntity`. | 3,4 | **M** (port) |
| **6** | AOI float retype (distance math + cell keys; radius value unchanged) | 0 | **S–M** |
| **7** | Combat: free-aim already continuous; decide + port melee (cone→sector or positional fan); candidate selection by radius. | 0,2 | **M** |
| **8** | Monster AI: steering + obstacle avoidance + Euclidean leash/aggro; spawner positions float. **Second-largest new surface.** | 2,7 | **L** |
| **9** | Interaction: adjacency → interaction radius (float); spawn/resource scatter in continuous space. | 0 | **S–M** |
| **10** | Persistence: float position columns + migration on SQLite **and** Postgres. | 0 | **S–M** |
| **11** | Test rewrite across all suites (port the `exp/` predictor tests; rewrite movement/AI/combat/interaction/persistence parity). | all | **L** |
| **12** | Stress re-baseline (standard 120/30s gate) + **bandwidth study** (float positions vs the per-client ceiling at 120–150 visible). Likely forces fixed-point + delta. | 3,5 | **M** |

---

## 3. What breaks at each phase + the risks

- **Phase 0–1 (position + integrator):** the whole game stops compiling until the `.Tile` retype lands — it is a
  big-bang seam edit by nature (243 sites). Movement behaves but with **no collision yet** (walks through walls),
  which is expected mid-migration.
- **Phase 2 (collision):** the highest-risk phase. Risks: snagging on cell corners, tunneling through thin walls
  at mount speed, wall-slide that feels sticky or slippery. **No experiment evidence exists here.** This is where
  schedule overruns live.
- **Phase 3 (protocol):** the **hard break**. The instant this lands, every old client and the collaborator's
  build are incompatible — coordinate the cutover. Bandwidth regression risk is real (float snapshot inflation);
  if the bandwidth study (Phase 12) comes back over the ceiling, fixed-point + delta becomes mandatory rework.
- **Phase 4 (prediction):** the spike's #2 risk. The *model* is proven by the experiment, but **sub-tile
  rubber-banding is more visible than tile snapping**, and the experiment ran without collision mispredicts — the
  exact thing that *causes* corrections. Real reconcile-against-walls feel is unproven; budget a tuning arc.
- **Phase 8 (AI):** monsters can now get physically stuck on geometry (couldn't before — tiles can't wedge).
  Greedy stepping needs avoidance; without it, monsters pile on corners. The experiment's open-field bot proves
  nothing here.
- **Float determinism:** tile prediction got *exact* integer parity. Float parity is "within tolerance," so the
  reconcile error budget becomes a tuning parameter rather than a guarantee — fuzzier by nature.

---

## 4. Key design decisions to resolve FIRST

These gate the work; decide them before committing, because they change the size of Phases 2, 7, and 8.

1. **Collision model:** swept-circle vs solid AABBs derived from the blocked-tile set (cheapest, keeps the
   existing map authoring) — or a navmesh (more work, enables real pathing)? Recommend starting from blocked-tile
   AABBs. Body radius is a new tuning knob.
2. **Combat — target-locked vs positional?** Does melee stay a tile-fan-shaped cone (port shape, keep feel) or go
   fully positional via the existing `FreeAimSector`? Free-aim already works; leaning positional is cheaper and
   more consistent with continuous movement.
3. **Monster pathing depth:** simple steering + local obstacle avoidance (v1-adequate, **L**) vs real pathfinding
   (navmesh/A\* on continuous space, **XL**)? Recommend steering-only for v1, navmesh deferred.
4. **Facing granularity:** keep 8-dir `Direction8` for animation (derive from velocity) vs go to a continuous
   facing angle? 8-dir is the cheap, art-compatible choice; keep it as an animation enum.
5. **Position encoding on the wire:** float32 (simple, fat) vs fixed-point (sub-tile precision, compact,
   delta-friendly)? Decide *before* Phase 3 so the protocol is built once. The bandwidth study (Phase 12) may
   force fixed-point regardless — design for it up front.
6. **Speed model:** speed as a pure server-owned stat (units/sec), confirming the anti-speedhack guarantee carries
   over without commit-step machinery. (The experiment already validated this shape.)

---

## 5. Honest effort estimate

| Phase | Size | Rough calendar (relative) |
|---|---|---|
| 0 Position retype | L (wide) | ~1 week |
| 1 Server integrator (port) | M | ~2–3 days |
| 2 **Collision** | L–XL | **1–2+ weeks, with feel iteration** |
| 3 Protocol + bump | M | ~3–4 days |
| 4 **Prediction (port + integrate + tune)** | L | **1–2 weeks** |
| 5 Remote interp (port) | M | ~3–4 days |
| 6 AOI retype | S–M | ~1–2 days |
| 7 Combat | M | ~3–5 days |
| 8 Monster AI (steering/avoidance) | L | ~1–2 weeks |
| 9 Interaction/scatter | S–M | ~1–2 days |
| 10 Persistence migration | S–M | ~1–2 days |
| 11 Test rewrite | L | ~1 week |
| 12 Stress + bandwidth study (+ likely fixed-point rework) | M | ~3–5 days |

**Bottom-line total: solidly XL — a multi-week milestone, realistically 6–10+ focused weeks**, dominated by
**collision feel (2)**, **prediction-against-walls feel (4)**, and **monster AI without a grid (8)** — the three
things the experiment did **not** exercise. The mechanical retyping (0, the protocol, persistence, AOI) is the
*cheap* part; the cost is in the three new behavioural surfaces and their tuning arcs (we spent the entire
S53→S103 arc getting *tile* prediction to feel right — continuous reopens a comparable surface).

**Opportunity cost.** This milestone consumes the calendar that would otherwise buy multiple shipped features —
crafting, more content, loot depth, etc. It produces **no new player-facing feature on its own**; its single
concrete payoff is **varied/continuous speed** (Albion-style mounts each a few percent apart), which the tile
model quantizes by construction. Everything else (smoothness, prediction) the tile game already achieves
adequately. So the question is narrowly: *is fine-grained speed variety worth a multi-week re-architecture right
now?*

---

## 6. Recommendation framing (the decision is the user's)

The experiment changed the calculus in one important way and **only** one: it proved the continuous *netcode model
feels right*, retiring the spike's #1 *feel* risk. It did not touch the three hardest *engineering* surfaces
(collision, AI, protocol). So the decision should turn on the following criteria.

**Migrate now if:**
- Varied, non-bracketed speed (mounts, slows, haste that read as smoothly different) is a **near-term design
  pillar**, not a someday-nice. Tiles fight this by construction; it is the one thing only continuous gives you.
- You are willing to **freeze tile-stepped `main`** (tag it) and accept a multi-week milestone with **no new
  feature** at the end other than the movement model itself.
- You accept that collision feel, prediction-against-walls feel, and grid-free AI are **unproven** and will need
  their own iteration arcs (the experiment is no evidence on any of the three).
- The hard protocol break is acceptable now (small client base / collaborator can cut over in lockstep) — the
  cost of breaking the wire only grows as more depends on it, so *if* you're going to do it, earlier is cheaper.

**Do NOT migrate now (keep iterating tiles) if:**
- The roadmap priority is **player-facing features** (crafting, content, loot depth). The migration spends weeks
  and ships none of them.
- Coarse speed brackets are tolerable for the foreseeable design. If you don't need Albion-style fine mounts soon,
  the defining payoff isn't being collected.
- You are not ready to commit to building **real collision and AI navigation** — these are not optional add-ons to
  continuous movement; they are load-bearing and currently absent.

**Lowest-regret path if undecided:** the de-risking that remains is **collision + prediction-against-walls**, not
the open-field feel (that's done). A cheap next probe — *before* committing the full milestone — is to extend the
experiment with **swept-circle collision + wall-slide against the real blocked-tile geometry, exercised at
100–150 ms injected latency**. If wall-slide and reconcile-against-walls feel right there, the dominant remaining
risk is retired for a few days' spike instead of mid-milestone. If they don't, you learned it cheaply. Tile-
stepped `main` stays frozen and tagged the entire time either way.

**One-line read:** the migration is now a *known, ported* movement-core swap plus **three genuinely hard new
systems the experiment never touched** (collision, grid-free AI, the protocol break). It is worth it **only if
fine-grained speed is a real near-term design goal** — otherwise it is a multi-week milestone whose only deliverable
is a movement model the tile game already approximates, paid for in shipped features.
