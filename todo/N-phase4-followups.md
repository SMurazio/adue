# Phase 4 review follow-ups (continuous predict/reconcile)

From the Phase 4 independent review. Finding **A** (the mid-session re-attach input-seq freeze) was BLOCK-class and is
FIXED in the Phase 4a commit (single persistent `_inputSeqHighWater` on `MmoClient`, seeded into each fresh predictor;
re-attach guard tests in `tests/Mmo.Client.Core.Tests/MmoClientReattachSeqTests.cs`). The items below are NON-BLOCKING
— correctness/coverage polish that guards against regression.

## B — predictor/harness tests integrate a HAND-ROLLED server, not the real path (N)
The timing-faithful harness (`tests/Mmo.Client.Core.Tests/ContinuousReconcileHarnessTests.cs`) and the predictor unit
tests (`ContinuousPredictorTests.cs`) both integrate the SERVER side with an inline integrator copied into the test —
NOT the production `GameServer.HandleMoveIntent` → `Zone.IntegrateMovement` → `WorldEntity.ComputeMoveDelta` path. The
two are intentionally the "same" math, but they are not byte-identical: production normalize divides (`X / len`)
whereas the harness multiplies by the reciprocal (`X * (1/len)`) — a sub-ULP difference, operationally harmless, but
no test proves the PRODUCTION server reconciles a real predictor to ≈0 correction.

**Fix:** add ONE integration test that drives the real `GameServer`/`Zone` (the socket harness in
`MmoClientIntegrationTests` is a template) with a client predictor, and asserts the predictor's reconcile correction
stays ≈0 (within the wire-quantization budget) over a steady walk — closing the "the test inherited the author's
model" gap with the real server in the loop. Natural companion to the existing
`RawDirectionNormalizeIntegrationTests` (Phase 3 follow-up A, already on the real server path).

## C — idle client send-gate (carry from `todo/N-phase3-followups.md` C) (N)
The Godot client sends a `(0,0)` `MoveIntent` every render frame while standing still (~60 Hz) — a constant
standing-still packet stream. The server dedups by seq and the dt-budget makes it harmless, but it's wasteful. Add an
idle send-gate: don't send while the input direction is zero AND already-stopped (send the stop once, then stay quiet
until input resumes). This is the SAME item as `todo/N-phase3-followups.md` item C, re-surfaced by the Phase 4 review;
do it once and drop both notes. Touches the Godot input/send path (`PredictAndSendMove` caller), not the predictor.

## D — fractional speed-multiplier mispredict (documented accepted, bound it) (N)
Client speed determinism holds EXACTLY only at speed-multiplier 1.0 with a tick-aligned cooldown: the predictor derives
its integrate speed as `1000 / EffectiveStepCooldownMs` (`MmoClient.DerivePredictorSpeed`), which is the TICK-QUANTIZED
cadence, whereas the server integrates at its unrounded `SpeedUnitsPerSecond`. Under a fractional `/speed` multiplier
the two differ by up to one tick-quant → a small STEADY drift the reconcile budget absorbs (a documented accepted
mispredict, not a freeze). No test bounds it.

**Fix (either):** (1) parametrize the timing-faithful harness over a FRACTIONAL multiplier and assert the steady
divergence stays within a stated bound (so a regression that widened it would fail), OR (2) replicate the server's
actual `SpeedUnitsPerSecond` to the client (e.g. on the speed-change / spawn retune) so the client integrates the
server's exact speed and the residual closes. (1) is the cheaper guard; (2) removes the mispredict entirely.
