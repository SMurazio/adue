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
| 1 | Server continuous integrator (port `ContinuousMover`/integrate-per-input) | ✅ **DONE** | `836befd`; review SHIP-WITH-FOLLOWUPS; cosmetic followup #1 done (`f41b8cb`); #2 (dead ClientSession members) → Phase 3 |
| 2 | Continuous collision (port `ContinuousCollision`; AABBs from blocked tiles) | ✅ **DONE** | `14c7fbe`; review SHIP (clean deterministic port — byte-identity verified for Phase-4 reuse; superset query, scope held) |
| 3 | Wire: float/fixed-point positions, continuous MoveIntent, drop StepCommit — **protocol-major** | ✅ **DONE** | Pass A `f6b1ffa` + Pass B `899200c` (v36); review SHIP-WITH-FOLLOWUPS (dt-budget sound, diagonal-normalize correct, wire clean); followups in `todo/N-phase3-followups.md` (A: normalize guard test → Phase 4; C: idle send-gate). **Wire is now continuous.** |
| 4 | Client prediction + reconcile (port `ContinuousPredictor`) | ✅ **DONE** | `ded8622` + Finding-A fix `29b3103`; review SHIP-WITH-FOLLOWUPS → caught a BLOCK re-attach seq-freeze (fixed + guarded); 3 determinism gaps resolved, timing-faithful harness green. Followups B/C/D in `todo/N-phase4-followups.md`. **Local player predicts smoothly.** |
| 5 | Remote interpolation/extrapolation (port `RemoteContinuousEntity`); retire hop/TileInterpolator | **PLANNING** | depends 3,4. Adds per-entity velocity to the wire (v37→v38) for remote dead-reckoning. Last phase before the drivable build. |
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
- **2026-06-25** — Phase 1 implemented + gated green + committed (`836befd`). Players integrate continuously
  server-side; commit-step/client-driven machinery deleted; monsters + client + wire untouched. Fork resolved:
  deleted 2 client-project commit-step harnesses (`TimingFaithfulReconcileHarnessTests`, `TailLossResendHarnessTests`
  — they tested the deleted commit-step model); their timing-faithful regression-guard lesson re-targeted to Phase 4
  (see the Phase 4 row). Gate: Shared 118 / Client.Core 209 / Server 304, godot-build clean. Independent review in flight.
- **2026-06-25** — **Phase 1 SHIPPED.** Independent review verdict SHIP-WITH-FOLLOWUPS — integrator math correct
  (diagonals normalized, instant stop, fixed Δt), deletions safe (surviving refs comment-only; wire types preserved),
  scope held (monsters + client untouched), invariants held (swing-root freeze, no StateRevision spam),
  anti-speedhack intrinsic. Followup #1 (stale dormant-speed comments + `RefreshDormantSpeedStat`→`RefreshSpeedStat`)
  done in `f41b8cb`. Followup #2 (dead `ClientSession` commit members) deferred to Phase 3 wire cleanup. Phase 2
  (collision) planning started.
- **2026-06-25** — **Phase 2 SHIPPED** (`14c7fbe`). Review verdict SHIP — clean deterministic resolver port to
  `Mmo.Shared` (line-by-line vs the spike: substeps/passes/epsilons/X-tie-break verbatim, Z→Y complete), byte-identity
  verified (the `IReadOnlyList` change is safe; row-major positional wall order, never HashSet iteration), the
  swept-neighborhood query is a strict superset (no tunnel), wall-block flip pinned, monsters/client/wire untouched.
  **Server-side continuous foundation complete (position + integrator + collision, all green + independently reviewed).**
  Phase 3 (the protocol-major wire break) planning started.
- **2026-06-25** — Phase 3 **Pass A** green + committed (`f6b1ffa`): additive `PositionEncoding` (Q12.4) +
  `EntityStateSnapshot.Tile`→`WorldVector Position` internal retype, WIRE UNCHANGED (v35 round-trips). Shared 146
  (+14) / Client.Core 209 / Server 310, godot clean. **Pass B (the v35→v36 atomic break + per-input server + dt-clamp)
  HELD for the user's explicit go-ahead** (the wire point-of-no-return + a new anti-speedhack decision).
- **2026-06-25** — User chose "Go". Phase 3 **Pass B SHIPPED** (`899200c`): the atomic v35→v36 break — fixed-point
  continuous positions, per-input `MoveIntent{seq,dir,dt}`, server per-input-by-dt integration, `LastInputSeq`,
  the wall-clock **dt-budget anti-speedhack**, all dead commit/mode/move-input machinery deleted (~2340 LOC), all 5
  clients flipped (Godot renders RAW; predictor/interp unwired-not-deleted). Review **SHIP-WITH-FOLLOWUPS** —
  dt-budget bound holds (a flood can't out-integrate real time; hostile dt neutralized), diagonal-normalize correct,
  fixed-point round-trips ≤1/16 u, deletions clean, monsters/scope respected. Followups: B done (comment), A+C tracked.
  **Phase 3 complete — the game runs on the continuous wire (client renders raw until Phase 4 prediction).**
- **2026-06-25** — Phase 4 implemented (the local-player continuous predictor — first live-playable build), gated green,
  awaiting independent review. Stage 0 (shared de-risk): extracted `TileWalls.NeighborhoodWallsForMove` (server
  `QueryNearbyWalls` is now a byte-identical forwarder — parity test pins it); replicated `BodyRadiusUnits` on
  `ServerHello` (**wire v36→v37**, intra-branch); lifted `MaxInputDtSeconds` to shared `ContinuousMovement`. Stage 1:
  ported `ContinuousPredictor` to `Mmo.Client.Core/Continuous` (Z→Y, SHARED resolver/walls, dt-clamp-and-buffer,
  `Reconcile(in WorldVector, uint)`, pinned consts). Stage 2 (the flip): `PredictAndSendMove` (predictor mints seq →
  send; retired `_moveSequence`), `AdvanceRender` once/frame, reconcile local entity vs `(Position, LastInputSeq)`,
  render seam (local = predicted RenderX/Y; remote/monsters RAW), respawn/AOI re-attach anchored to confirmed Position,
  live speed retune on `MovementSpeedChanged`, F5 `Prediction` A/B toggle. Stage 3: timing-faithful reconcile harness
  (real 20Hz server integrate + Q12.4 + latency/jitter/drop, 144Hz client) — 5 invariants green. Stage 4: ported
  predictor unit tests + collision-slide + **Followup A** (server raw-dir-normalize guard). Stage 5: deleted the
  obsolete tile `LocalPlayerPredictor` (+ tests + dead plumbing, ~2620 LOC), kept `TileInterpolator`/
  `MonsterHopInterpolator` (Phase 5) + `MovementCadence.EffectiveStepCadenceMs`. Gate: Shared 141 / Client.Core 160 /
  Server 313, godot-build clean.
