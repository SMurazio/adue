# TEST1 — timing-faithful headless reconcile/feel harness (the test UO5 should have had)

PRODUCTION on `review/tile-step-todo`. **Goal: close the movement-feel test blind spot.** Our predictor unit
tests run an IDEALIZED tick timeline, so they pass while the rendered feel is broken — that's exactly how UO5
(`9cf3abf`, reverted `1bb3ad6`) shipped 237/237-green but felt "much worse" live. The root: the tests don't model
**20Hz snapshots arriving against a ~150ms (3-tick) confirm cadence**, where `serverStepSeq` naturally advances
only on ~1 of every 3 snapshots during normal walking — the precise condition the UO5 stall counter misread.

## Build a reusable, timing-faithful harness
A headless rig (no Godot) driving the REAL `LocalPlayerPredictor` + a REAL server-step model (`WorldEntity`) on a
wall clock, modelling real timing:
- **Snapshots at 20Hz** (every 50ms), each re-anchoring the predictor (`Reconcile` with the server's confirmed
  tile + `RecipientStepSeq`).
- **Steps confirm at the cadence** (3 ticks / 150ms) on the server model — so `serverStepSeq` advances ~every 3rd
  snapshot, NOT every snapshot.
- Configurable **latency** (deliver snapshots delayed by RTT), **jitter**, and **drop** (skip a snapshot — must
  still recover via the cumulative `RecipientStepSeq` on the next one).
- Records `ReconcileOutcome` (Matched/Corrected/Snapped) counts AND the render position over time (to assert
  monotonic forward glide / detect backward jumps).
- Parameterised by render mode (`Predicted`, `UoClientDriven`) and direction.

## Invariants to assert (these must be GREEN on the CURRENT reverted code)
1. **Steady normal walking does not cap/snap** (the UO5-catching guard): hold a direction for a sustained run at
   50ms and 100ms latency in `UoClientDriven` (and `Predicted`) → **zero `Corrected`/`Snapped`** over the run, the
   lead is stable, and the render glides **monotonically forward** (no backward tile jumps). Cover all 8
   directions + a long straight run. (This FAILS on the UO5 code and PASSES on current — that's the proof it
   guards the regression.)
2. **A genuine reject DOES snap**: predict into a tile the server refuses (wall / not walkable) → the reconcile
   `Corrected`/`Snapped`s and the render pulls back. Correction must still happen when it SHOULD.
3. **A dropped snapshot self-heals**: skip a snapshot mid-run → the next one's cumulative `RecipientStepSeq`
   re-syncs with no permanent desync.

## Explicitly OUT of scope here (it's the UO5 re-attempt's job)
Do NOT add the "frame-drop overshoot converges back" test — on the current (reverted) code that bug is UNFIXED, so
that test would be red. This task delivers the RIG + the green invariants above; the UO5 re-attempt then uses this
rig to drive its fix red→green. Note in the harness where that future test slots in.

## Gates
- `run-checks.cmd` green (the new harness + its tests). This is test-only — no production code changes, so
  `godot-build` shouldn't be needed (note if you touch anything client/Godot).
- **Do NOT run `stop-mmo`/any gate that force-kills a running server** — the user may have a session up. If
  `run-checks` fails on a `Mmo.Shared.dll` lock (server running), report it and leave the work for the
  Orchestrator to gate (coordinating timing). If `git` is denied, leave the work + a review-request.

## Standing rules
One discrete revertable commit referencing this task; delete this file in that commit. **Safe Local Execution**.

## Acceptance
A reusable timing-faithful headless harness exists; the three invariants above are green on the current code; it's
structured so the UO5 re-attempt can add its overshoot-converges test against the same rig. `run-checks` green.
