# Phase 4 — Client-Side Continuous Predictor (implementation spec)

Part of the continuous migration. Base: Phase 3 (continuous wire v36; client sends per-frame `MoveIntent{seq,dir,dt}`,
decodes continuous `Position`+`LastInputSeq`, renders LOCAL player RAW; predictor/interp unwired). Phase 4 = the LOCAL
player PREDICTS (smooth, zero-lag); remote stays raw (Phase 5). First live-playable continuous build.

## Three determinism gaps (the "no-correction" crux) — resolved
1. **Walls:** the server's swept-AABB box-math (`TileGrid.QueryNearbyWalls`) is server-only. **Extract it to shared
   `Mmo.Shared.Domain.TileWalls.NeighborhoodWallsForMove(blocked, start, delta, radius, out)`**; `QueryNearbyWalls`
   becomes a forwarder (server byte-identical, assert it). The predictor calls the SAME shared helper against
   `ZoneModel.BlockedTiles` → identical wall set.
2. **Body radius:** server reads `_tuning.BodyRadiusUnits` (admin knob, default 0.5); client only has
   `CollisionDefaults.BodyRadius`. **Replicate it on `ServerHello`** (a 1-field header add — bump the wire version;
   it's intra-branch, no deployed clients). Client feeds it to the predictor. (A silent default-assumption desyncs at
   walls the moment the knob moves.)
3. **Speed:** server integrates at un-quantized `SpeedUnitsPerSecond`; the wire carries tick-quantized `StepCooldownMs`.
   **Derive `speed = 1000/EffectiveStepCooldownMs`** — EXACT at multiplier 1.0; at fractional multipliers the residual
   is bounded by one tick-quant and ABSORBED by the 1/16-u reconcile budget (documented accepted mispredict, not a
   bug). Escalation (a raw speed byte) deferred to a followup, NOT built now.

## dt alignment (R4 — killed by construction)
Lift `MaxInputDtSeconds = 0.25` to a shared constant (`Mmo.Shared.Domain.ContinuousMovement`); server, predictor, AND
`MmoClientRoot` reference it. The predictor clamps its predicted dt to it AND buffers the clamped dt → buffered dt ==
sent dt == server-integrated dt under normal play → replay reproduces the server path. The wall-clock dt-BUDGET (0.4s
burst) is NOT modeled in the predictor (only bites under sustained lag); the reconcile absorbs that one case
(predicted ≠ integrated) — verified bounded/convergent by the harness.

## Reconcile tolerance
Server quantizes `Position` to Q12.4 on-send (≤0.0625 u/axis). `SnapThresholdUnits = 4.0` ≫ that → quantization NEVER
snaps; the offset-decay (`RenderCorrectionPerSecond = 14`) smooths ≤0.088 u over ~70 ms → invisible. Locked by a harness
assertion (zero snaps on a quantization-only steady walk).

## The work (staged sub-commits)
0. **`feat(shared): extract NeighborhoodWallsForMove + replicate BodyRadius on ServerHello + lift MaxInputDtSeconds`**
   — additive, server byte-identical (assert `QueryNearbyWalls == NeighborhoodWallsForMove`; `ServerHello` radius
   round-trip). Bump the wire version for the ServerHello add.
1. **`feat(client): port collision-aware ContinuousPredictor`** (`src/Mmo.Client.Core/Continuous/ContinuousPredictor.cs`)
   — near-verbatim port of `exp:ContinuousPredictor` (Z→Y), using the SHARED `ContinuousCollision` + querying walls
   per-move via `NeighborhoodWallsForMove` into a reused scratch list. `Reconcile(in WorldVector serverPos, uint
   lastInputSeq)`. dt-clamp inside `PredictAndBuffer`. Pinned consts verbatim (Snap 4.0, MaxBuffer 256, Correction 14).
   Pure, unwired.
2. **`feat(client): predict the local player`** — THE behavioural flip. `MmoClient.PredictAndSendMove(dir, dt)`:
   `seq = _predictor.PredictAndBuffer(...)` THEN `Send(MoveIntent(seq,dir,dt))` (predictor mints the seq — sent==buffered;
   retire `_moveSequence`); `AdvanceRender(frameDt)` once/frame. Reconcile the LOCAL entity against `(state.Position,
   snapshot.LastInputSeq)`. `ClientEntity.ToRenderState`: local+attached → predictor `RenderX/Y`; all others → raw
   (Phase 5). Targeting still reads confirmed `LocalTile`, NEVER predicted (preserve S53). Re-attach predictor on
   respawn/AOI re-entry anchored to the fresh confirmed `Position`. Live-update speed on `MovementSpeedChanged`.
   `PredictionEnabled` F-key toggle → A/B raw-vs-predicted for feel testing.
3. **`test(client): timing-faithful continuous reconcile harness`** (the Phase-4 MUST — replaces the deleted
   UO5/NET2/NET3 guard). Models real 20Hz server integration of the buffered `{dir,dt}` inputs + fixed-point-quantized
   snapshot delivery at LATENCY/JITTER/DROP; client polls ~144Hz (predict+AdvanceRender each frame, Reconcile on
   delivery). Assert: (1) **zero corrections during steady walking** (no-loss==no-correction, WITH collision + fixed-
   point); (2) render glides monotonic, never retreats on stop; (3) **zero corrections reconciling against a REAL wall**
   (the Phase-2 payoff vs real geometry); (4) drop/jitter → buffer bounded, re-syncs, converges no oscillation;
   (5) dt-budget bite under sustained lag → bounded + convergent.
4. **`test: port ContinuousPredictor unit tests + server raw-dir-normalize guard (Followup A)`** — port `exp` predictor
   tests (Z→Y) + a collision slide unit test; Followup A: a server-path test that a raw non-unit `MoveIntent`
   (`1,1` / `10,0`) integrates the SAME distance as cardinal `(1,0)` (guards `rawDir.Normalized()` against regression).
5. **`refactor(client): delete obsolete tile LocalPlayerPredictor + dead plumbing`** — delete `LocalPlayerPredictor.cs`
   (943 lines) + its tests + the dead `ClientEntity._predictor`/`AttachPredictor`/`CalibrateToServerTick`/tile-Reconcile
   plumbing + tile-only `MovementCadence`/`StopOnReversal`/`PredictedLocalTile`. KEEP `TileInterpolator`/
   `MonsterHopInterpolator` (Phase 5 deletes them) + `MovementCadence.EffectiveStepCadenceMs` (Stage 0c speed). Own
   commit AFTER the flip is green.
6. **`docs: Phase 4 progress`**.

## Risks
- **R-determinism (radius/speed/walls) — dominant.** Mitigated by Stage 0 (shared wall-query, replicated radius,
  exact-at-default speed). The harness's "zero corrections at a real wall" is the proof. A wall correction in feel
  testing ⇒ suspect radius/speed/wall-order, NOT the predictor math.
- **R-dt-budget:** the one place predicted ≠ integrated (sustained lag); reconcile absorbs it (harness inv. 5); the most
  likely source of any "rubber-band under sustained loss" report.
- **R-feel:** sub-tile rubber-band is more visible than tile snapping; reconcile-against-real-walls unproven in feel
  (experiment was open-field). Budget a tuning arc on `RenderCorrectionPerSecond`/`SnapThresholdUnits`; the
  `PredictionEnabled` A/B toggle is the lever. (Human-only live check.)
- **R-respawn/AOI lifecycle:** re-attach the predictor cleanly on respawn/AOI re-entry to the fresh confirmed position
  (the S47b-class guard) or a stale predictor drives a removed entity.
