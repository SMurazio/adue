# Continuous-Movement Migration — Progress Tracker

**Branch:** `feat/continuous-migration` (off `main` @ `7a039d7`). **Fallback:** `main` stays tile-stepped; tag
`tile-stepped-stable` marks the frozen good tile build (the A/B comparison point). Both branches stay available.

**Plan of record:** `docs/continuous-migration-roadmap.md` (the full scope, inventory, risks, estimate). This file
is the live STATUS tracker for the 12 phases.

**Loop discipline (per phase):** implement (subagent or direct) → orchestrator runs gates (`run-checks` +
`godot-build`, stress where relevant) → independent reviewer subagent (symptom + diff, not the plan) for any
behavioral phase → ONE revertable commit per sub-unit. **The branch must compile + tests green at every phase
boundary** (no half-migrated commits that don't build). Movement is high-risk netcode → full rigor throughout.

## Resolved design decisions (orchestrator calls, per roadmap §4)

- **Position model:** entity position becomes continuous `WorldVector` (float X/Y) + velocity. The **MAP stays a
  tile grid** (`TileGrid` / blocked-tile set) — only ENTITY positions go continuous; Phase 2 derives collision
  AABBs from the blocked tiles. (Phase 0 may refine; see the Phase 0 plan.)
- **Collision:** blocked-tile → solid AABBs; **shared deterministic sub-stepped circle-vs-AABB with wall-slide +
  anti-tunneling** — already built + proven in the spike (`ContinuousCollision`, exp branch). Port it.
- **Speed:** a **server-owned stat (units/sec)** replacing tick-quantized cadence. Carries the anti-speedhack
  guarantee without commit-step machinery.
- **Combat:** go **positional** via the existing `FreeAimSector` (already continuous geometry on tile-centre
  coords) rather than keep the tile-fan cone — cheaper + consistent.
- **Monster AI:** **steering + local obstacle avoidance** for v1; full navmesh pathfinding deferred.
- **Facing:** keep `Direction8` as an **animation enum** (derive from velocity heading); no longer the movement unit.
- **Wire encoding:** **design for fixed-point** positions (+ delta-vs-baseline) up front — the bandwidth study
  (Phase 12) may force it at 120–150 visible entities; build the protocol once.

## Phase status

| # | Phase | Status | Notes |
|---|---|---|---|
| 0 | Position type + speed stat; retype ~243 `.Tile` sites | ✅ **DONE** | `9fdc65a`; gate green (Server 329/Core 251 unchanged, Shared +15); independent review SHIP (clean behavior-frozen seam) |
| 1 | Server continuous integrator (port `ContinuousMover`/integrate-per-input) | **IN PROGRESS** | plan: `docs/migration/phase-1-plan.md`; PLAYERS-only continuous (monsters tile-stepped till Phase 8), no collision till Phase 2, wire tile till Phase 3 |
| 2 | Continuous collision (port `ContinuousCollision`; AABBs from blocked tiles) | pending | depends 1 — proven in spike |
| 3 | Wire: float/fixed-point positions, continuous MoveIntent, drop StepCommit — **protocol-major** | pending | depends 0,1 |
| 4 | Client prediction + reconcile (port `ContinuousPredictor`) | pending | depends 1,3 |
| 5 | Remote interpolation/extrapolation (port `RemoteContinuousEntity`); retire hop/TileInterpolator | pending | depends 3,4 |
| 6 | AOI float retype | pending | depends 0 |
| 7 | Combat: positional via FreeAimSector | pending | depends 0,2 |
| 8 | Monster AI: steering + avoidance; Euclidean leash/aggro | pending | depends 2,7 |
| 9 | Interaction: adjacency → interaction radius; continuous scatter | pending | depends 0 |
| 10 | Persistence: float position columns (SQLite + Postgres) | pending | depends 0 |
| 11 | Test rewrite across suites (port exp predictor/collision tests) | pending | depends all |
| 12 | Stress re-baseline (120/30s) + bandwidth study (likely fixed-point) | pending | depends 3,5 |

## Log

- **2026-06-25** — Migration approved (collision spike de-risked the last unknown). Branch + tag created; Phase 0
  planning dispatched. Design decisions resolved per roadmap §4 (above).
- **2026-06-25** — Phase 0 planned (`docs/migration/phase-0-plan.md`). Correction: `WorldVector` does NOT exist
  (roadmap was wrong) — Phase 0 creates it as `record struct(double X, double Y)` (double, to match the proven
  experiment's determinism; NOT the roadmap's float). Phase 0 = behavior-frozen tile-center-valued retype; existing
  tests stay green via accessor-rename only. Implementation started.
- **2026-06-25** — **Phase 0 SHIPPED** (`9fdc65a`). Gate green (build OK; Server 329 / Client.Core 251 unchanged;
  Shared 108 +15 WorldVector tests; godot-build clean). Independent reviewer verdict **SHIP** — all six axes clean
  (no test expected-value changed, integer math parity held, exact round-trip, dormancy confirmed, surfaces frozen,
  double). Phase 1 (server continuous integrator — the first real behavioral change) planning started.
