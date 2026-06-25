# Phase 1 — Server Continuous Integrator (implementation spec)

Part of the continuous migration. Base: green Phase-0 seam (`WorldEntity.Position` is a tile-centre-valued
`WorldVector`; `Velocity`/`SpeedUnitsPerSecond` dormant). **First behavioural phase** — tile-step parity breaks by
design. **Scope: PLAYER movement only.** Monsters stay tile-stepped (Phase 8). No real collision (Phase 2).

## The model
Per server tick, per authenticated moving player:
```
Velocity = MoveIntentDirection.ToUnitVector() × entity.SpeedUnitsPerSecond   // unit dir × server speed stat
Position += Velocity × Δt                                                     // Δt = 1.0 / TickRate (FIXED)
released/not moving: Velocity = Zero   (instant stop, no inertia)
```
Direct port of the proven `exp:ContinuousMover.Step` (Z→Y, on `WorldVector`), **no-walls path** (walls = Phase 2).
Fixed server Δt (matches the experiment server's autonomous-bot fixed-Δt path). Anti-speedhack is intrinsic: the
client's `MoveIntent` carries ONLY a `Direction8` (no magnitude, no timing); the server owns speed + Δt.

## Held-intent → velocity flow
Rewrite `GameServer.StepHeldMovementIntents` → `IntegrateHeldMovementIntents` (same call site, `TickCore`):
authenticated + moving → `entity.IntegrateMovement(dir.ToUnitVector(), dtSeconds, _zone)`; released/dead/keepalive-
timeout → `entity.StopMovement()`. New `WorldEntity.IntegrateMovement` (set Velocity, `Position += V·Δt`, face from
dir) + `StopMovement` (Velocity = Zero). New `Zone.IntegrateMovement` wrapper migrates the spatial index **only when
`ToTileRounded()` changes** (grid stays integer-keyed — Phase 6 floats it). New `Direction8.ToUnitVector()` →
normalized `WorldVector` (diagonals length-1, not faster).

## Wire stays tile — NO client change
`ToEntityStateSnapshot` still emits `entity.TileCoord` (= `Position.ToTileRounded()`), protocol v35 unchanged.
Client decodes tiles, renders tile-stepped — zero client edits. **Bump `StateRevision`/`StepSequence` only on a
rounded-tile crossing** (not every sub-tile tick) → snapshot bandwidth identical to today. At default speed
(`base = 1000/StepCooldownMs`) one tile = exactly `StepCooldownMs`, so the tile-crossing cadence ≈ today's; the
(expected, temporary) degraded feel + the sub-tile smoothness only arrive with Phases 3–5. Fractional
`SpeedMultiplier` is where continuous already differs (the whole point).

## Collision — NONE in Phase 1 (walk through walls, expected)
Real swept-circle is Phase 2 (port `ContinuousCollision`). A cheap tile-walkability clamp does NOT compose with
continuous motion (snags, no slide) — don't. Player integrator does not call `IsWalkable`. `IsStepWalkable`/grid
survive (monsters still use them).

## Delete now vs dormant
**Delete (server logic):** `WorldEntity.TryCommitStep`/`TryCommitStepAuthored` + `Zone.TryCommitStep*`;
`HandleStepCommit`/`HandleStepCommitBatch`/`HandleAuthoredStepCommit`/`ExtractFreshStepCommits`; `CommitAcceptFraction`
+ NET2/NET3 authored-tick constants; `_lastStepTick`/`_lastAuthoredCommitTick`; DIAG1 commit counters; the
client-driven pacing skip + `MovementMode` pacing. The player cooldown gate (`EffectiveStepCooldownTicks` in the
player path).
**Dormant (wire types survive for Phase 3):** the `StepCommit*` / `MovementMode` message dispatch arms become no-ops.
**KEEP (load-bearing):** `WorldEntity.TryStep` + `IsStepWalkable` + `EffectiveStepCooldownTicks` + `_nextEligibleTick`
for the MONSTER tile-step path (Phase 8) AND the attack-movement-root freeze. **Retain the freeze check** in the
player integrator (skip integration while `serverTick < _nextEligibleTick`) so swing-root still roots players (R2 —
a combat invariant the combat tests assert).

## Tests (Phase-11 work pulled forward, movement only)
- **DELETE:** `WorldEntityCommitStepTests`, `StepCommitBatchIngestTests`, `CommitCursorDecoupleTests`,
  `ClientDrivenMovementIntegrationTests` (the commit-step system is gone).
- **REWRITE (tile-step → continuous):** `WorldEntityMovementTests` (assert `Position += speed·Δt`, diagonal same
  speed, instant stop), `ServerMovementTraceTests`, `MovementSpeedCommandIntegrationTests` (multiplier → per-tick
  distance, not cadence).
- **NEW:** `WorldEntityIntegratorTests` (port `exp:ContinuousMoverTests` — linear advance, diagonal normalization,
  instant stop, multiplier→distance, tile-crossing bumps seq/revision) + a GameServer integration test (held intent
  advances Position continuously while the snapshot still emits a quantized tile).
- **CLIENT tests UNCHANGED** (still tile: `LocalPlayerPredictorTests`, `TileInterpolatorTests`,
  `MonsterHopInterpolatorTests`, cadence) — they're replaced in Phase 4, not now.

## Sub-commits (each compiles + green; 3+4 may merge — the flip + its test rewrite are inseparable)
1. `feat(shared): Direction8.ToUnitVector()` (+ test). Additive.
2. `feat(server): WorldEntity.IntegrateMovement/StopMovement + Zone.IntegrateMovement` (+ `WorldEntityIntegratorTests`).
   Additive, not yet wired. Green.
3. `feat(server): switch player movement onto the continuous integrator` — the behavioural flip (`TickCore` →
   `IntegrateHeldMovementIntents`); keepalive + swing-root-freeze retained; monsters untouched. Fold the
   `WorldEntityMovementTests` rewrite in (the flip fails tile-parity tests).
4. `refactor(server): rewrite/delete tile-step + commit-step tests`.
5. `refactor(server): delete commit-step + client-driven machinery (dormant the wire dispatch arms)`.
6. `docs: Phase 1 progress`.

## Risks
- **R4 (watch for Phase 4):** fixed server Δt (`1/TickRate`) must equal the Δt the Phase-4 client predictor replays
  with, or reconcile drifts. Phase 3/4 introduce per-input-seq+dt `MoveIntent` (the experiment's model) — the
  migration MUST converge there; don't let Phase 4 silently inherit a mismatched Δt.
- R1 StateRevision/seq spam → bump on rounded-tile change only.
- R2 swing-root freeze → retain the `_nextEligibleTick` check in the integrator.
- R3 monster/player divergence → monsters use `TryStep` (Velocity stays Zero); players use `IntegrateMovement`; never both on one entity.
- R5 rounding-boundary jitter at exactly x.5 → assert the boundary in tests (no per-tick tile oscillation).
- R6 stop semantics → released/keepalive MUST call `StopMovement()` (zero velocity) or the entity glides forever.
- R7 "walks through walls" is EXPECTED (roadmap §3) — delete/defer any test asserting a player can't cross a blocked tile (Phase 2), don't "fix" it.
