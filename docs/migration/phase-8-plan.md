# Phase 8 — Continuous Monster AI (slimes keep hopping) — implementation spec

Part of the continuous migration. Base: Phases 0–7,9 done. **DECISION (user): slimes keep HOPPING** — discrete
leaps + pause, `Velocity` stays ~0, `Position` updates SPARSELY (once per hop). Phase 8 makes only the NAVIGATION
continuous. Server-only (Option A); no client work — see "the jump look" below.

## The hop model
A **discrete collision-valid leap of `HopDistanceUnits` (default 1.0 tile) toward the nav target, once per
`EffectiveStepCooldownTicks`, applied via `WorldEntity.ApplyResolvedMove`, with `Velocity` left at Zero** (preserves
the sparse-update jump; keeps monsters OFF the player velocity-glide path — `Velocity` stays dormant for monsters).
**Instant leap** (one Position write on the cadence tick; Position unchanged on intermediate ticks — reproduces
today's tile-step sparse cadence). The hop **arms `_nextEligibleTick`** itself now (the AI no longer goes through
`TryStep`): arm `serverTick + cooldown` on an accepted hop; leave it untouched (re-try next tick) on a fully-blocked
hop — replicate `TryStep`'s exact cadence rule, pin it with a unit test.
- **Collision-valid landing:** `delta = hopDir·HopDistanceUnits` → `TileGrid.QueryNearbyWalls` → `ContinuousCollision.Resolve(from, delta, radius, walls)` (slide/stop, anti-tunnel) → `ApplyResolvedMove(landing)`. The continuous S75-corner-cut analog, for free. Same body radius as players (`ServerTuning.BodyRadiusUnits`).
- **Movement-style seam (future gliders, NOTED not built):** a one-method `IMonsterLocomotion.Advance(...)`; ship ONLY `HopLocomotion`. A future `GlideLocomotion` (sets Velocity, integrates per-tick) slots in without touching the nav state machine. Do not over-build.
- **Per-type knobs:** `MonsterType.HopDistanceUnits` (1.0) + `AttackRangeUnits` (1.5), wired through `MonsterTypeRegistry`/`BuildTunables` like the other live per-type values.

## Continuous navigation (state machine preserved; metric/destination/step change)
Distances → **Euclidean float** on `WorldVector`. Conversion table (document in the `MonsterRoamAi` header — auditable):
| Range | today (Chebyshev) | Euclidean | note |
|---|---|---|---|
| Aggro | 6 | **6.0** | cardinal-preserving; diagonal corners trimmed to a TRUE circle (intentional tightening) |
| De-aggro | ⌈1.5·aggro⌉=9 | **9.0** (keep the ⌈1.5·aggro⌉ rule on the float) | preserve hysteresis ratio |
| Chase leash | 12 | **12.0** | soft bound (one-hop overshoot allowed) |
| Roam | 4 | **4.0** | roam destination sampled in the Euclidean disc |
| Attack/adjacency | 1 (3×3) | **1.5** | √2-covering so the diagonal still hits (1.0 would REGRESS) |
- **Roam destination** = a random continuous `WorldVector` in the Euclidean disc of `RoamRadius` around the spawner home, validated walkable (`_isWalkable(point.ToTileRounded())`); keep the bounded-probe→deterministic-scan fallback so a boxed-in monster terminates.
- **Chase** targets the player's live continuous `Position`; de-aggro re-reads it each hop. `FindMonsterAggroTarget` keeps the (tile-keyed) spatial-grid gather as a COARSE pre-filter but the range/nearest test goes Euclidean on `Position` — **pass `⌈AggroRadius⌉(+1)` to the gather so no in-range target is pre-filtered out**.
- **Hop direction** = true unit vector `(target - from).Normalized()` (not the `Direction8` greedy snap); set facing from it via the existing `FacingFromUnit` (facing stays 8-way for the sprite).

## Obstacle avoidance + livelock
1. **Resolve-slide (primary):** the hop routes through `ContinuousCollision.Resolve` → slides along walls (the continuous corner-cut). Handles the common case free.
2. **Clear-direction fallback (secondary):** if a resolved hop makes near-zero progress (slide hit a perpendicular wall), try a small fixed fan (±45°, ±90°) and take the first with positive progress. Lightweight local steering, deterministic order. NOT a navmesh.
3. **Livelock watchdog (KEEP the mechanism, re-base the trigger):** `NoProgressTimedOut` — "progress" = a resolved landing moved ≥ epsilon (~0.1u) toward the target; if none for ~2 hop-windows+margin → Chasing→`BeginReturnHome`, Roaming/Returning→`GoIdle`+re-pick. **CRITICAL: "resolved landing within epsilon of from" counts as NO-progress (not a cooldown wait)** so the watchdog always eventually fires at a slide fixpoint.

## TryStep disposition (delete the dead tile-step path — LAST sub-commit)
Monsters were the only production caller of `Zone.TryStep`→`WorldEntity.TryStep`→`IsStepWalkable`. After the AI is on
hops: **delete** `WorldEntity.IsStepWalkable` (S75 corner-cut, superseded by the resolver), `WorldEntity.TryStep` (both
overloads — the last tile-snapping Position write), `Zone.TryStep` (both), `MovementStepResult` (if monster-only —
verify). **KEEP** `_nextEligibleTick` + `EffectiveStepCooldownTicks` (player attack-root freeze + monster hop cadence).
Do it as the last sub-commit (independent revert); grep-confirm only test helpers reference `TryStep` after.

## The jump look (the user's concern) — Option A (server-only), verify live
`RemotePositionInterpolator` lerps between sparse Position samples. With monster hops the samples stay SPARSE (one per
~417ms cadence) and the playout delay is small relative to that, so the interp STARVES → HOLDs → catches up on the next
sample = the **hold-then-jump that IS the jump the user already sees** post-Phase-5. Phase 8 changes only the hop TARGET
(sub-tile), not the sparse cadence, so the jump is preserved. **Ship Option A (server-only, no client change); the
USER feel-tests the look.** Contingency **Option B** (only if it reads too smooth): a per-entity client "hop style" that
plays a short vertical arc on a Velocity=0 monster's sample jump (the retired `MonsterHopInterpolator` behavior, gated by
entity kind) — a FOLLOW-UP, not built now.

## Sub-commits (each compiles + green; behavioral flip isolated to #3)
1. `feat: hop primitive + IMonsterLocomotion seam + HopDistanceUnits/AttackRangeUnits knobs` (unit-test cadence + collision-valid; AI still calls TryStep).
2. `refactor: monster nav metrics → Euclidean (WorldVector home/dest, disc roam, range table)` (hop still tile-snaps via the old stepper).
3. `feat: switch the AI to the hop primitive (continuous sub-tile landings + resolve-slide)` — the behavioral flip.
4. `feat: clear-direction avoidance fallback + Euclidean-displacement livelock watchdog`.
5. `test: rewrite MonsterRoamAiTests to the continuous/collision-valid/sub-tile contract`.
6. `refactor: delete the dead tile-step path (Zone/WorldEntity.TryStep, IsStepWalkable, MovementStepResult)`.

## Tests (rewrite `MonsterRoamAiTests` — the injected stepper becomes a hop lambda)
Euclidean leash never exceeded (+one-hop tolerance); mostly-still hop fraction; **hops land collision-valid (the
headline — Position never inside a blocked AABB within body radius, never crosses a wall between hops)**; **some hops
land sub-tile** (Position != ToTileRounded — proves continuous nav); chase Euclidean-converges then attacks at
AttackRangeUnits; de-aggro at the Euclidean De-aggro/leash; livelock guard fires when wedged (resolve-slide+fallback
both stall) and never penetrates a wall; determinism per seed.

## Risks
1. **Jump look** — Option A preserves it (sparse cadence unchanged); user verifies live; Option B is the contingency.
2. **Wedging** — resolve-slide + fan fallback + the epsilon-progress watchdog (which MUST treat a slide fixpoint as no-progress).
3. **Leash/aggro feel** — the square→circle conversion trims diagonal corners (documented, intentional); AttackRange=1.5 avoids the adjacency regression; per-type radii are live-tunable to compensate.
4. **Cadence regression** — the hop primitive owns the `_nextEligibleTick` arm/re-try `TryStep` did; replicate exactly + unit-test.
5. **Determinism** — seeded RNG + all-double resolver; fixed fan/disc-sample order; keep the determinism test green.
6. **Aggro pre-filter** — the tile-keyed gather radius must be ≥ ⌈Euclidean aggro⌉ so no in-range target is dropped pre-filter.
