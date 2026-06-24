# Review request — TEST1: timing-faithful headless reconcile/feel harness

## Intent
Close the movement-feel test blind spot that let UO5 (`9cf3abf`, reverted `1bb3ad6`) ship 237/237-green
but feel "much worse" live. Build a **reusable, timing-faithful, headless** rig that drives the REAL
`LocalPlayerPredictor` + the REAL server `WorldEntity` step model on a wall clock, modelling real timing
(20 Hz snapshots vs a 3-tick / 150 ms step-confirm cadence, so `serverStepSeq` advances only ~every 3rd
snapshot during normal walking), plus configurable latency / jitter / drop. Assert the three green
invariants the task specifies; leave a marked slot for the UO5 re-attempt's (out-of-scope, currently-red)
overshoot-converges test.

This is **test-only** — no production code touched. `godot-build` not needed (nothing client/Godot changed).

## Branch / base
- Branch: `review/tile-step-todo` (production).
- Base: `17724f0` (current HEAD per session start). No commits made (see Gate status below — shells denied).

## How to diff
One new file, no edits to existing files:

- `tests/Mmo.Client.Core.Tests/TimingFaithfulReconcileHarnessTests.cs` (new)
- `todo/TEST1-timing-faithful-reconcile-harness.md` — **NOT yet deleted** (would be deleted in the commit;
  could not commit, see below).

## Change manifest
New test file `TimingFaithfulReconcileHarnessTests.cs` containing:

### The rig (reusable)
- `NewRig(start, facing, clientDriven, blocked?, latencyMs, jitterMs, dropTicks?)` — wires a real `TileGrid`,
  a real `WorldEntity` (server), and a real `LocalPlayerPredictor` (client) sharing the same start/facing,
  20 Hz tick (50 ms), 150 ms cadence (3-tick cooldown).
- `RunStraightRun(rig, held, runMs, clientDriven)` — walks the wall clock at ~144 Hz frames. Per frame:
  1. **Server**: for each elapsed 50 ms tick boundary, either `TryStep` (server-paced) or applies queued
     `TryCommitStep` commits (client-driven). One snapshot `(serverTick, Tile, StepSequence)` is produced per
     tick and queued, delivered after `latency (+ jitter)` unless its tick is in `DropTicks`.
  2. **Delivery**: every snapshot whose delivery time has elapsed is applied in the exact `MmoClient.ApplySnapshot`
     order — `CalibrateToServerTick(serverTick, receivedAt)` then `Reconcile(tile, recipientStepSeq, receivedAt)`.
     Outcomes (Matched/Corrected/Snapped) and the predicted-vs-server lead are tallied.
  3. **Client render frame**: `predictor.Tick(now)` (server-paced) or `Tick(now, buffer, out count)` +
     queue one commit per accepted step (client-driven, delivered to the server after uplink latency). The
     present-time render position is recorded into a per-frame trace.
- `RunResult` records Matched/Corrected/Snapped counts, the full render trace, final server/predicted
  tile + step-seq, and the max observed lead.
- Helpers: `MaxBackwardStep` (worst backward render delta along a travel axis — the monotonic-glide check),
  `AxesFor` (per-direction monotonic axes incl. diagonals), `BlockColumnFrom`.

### The three invariants (the task's required asserts)
1. `SteadyWalk_NoCapNoSnap_RenderGlidesForward` — `[Theory]` over **all 8 directions × {50 ms, 100 ms}
   latency × {Predicted, UoClientDriven}** (32 cases). 3 s sustained hold. Asserts **zero Corrected, zero
   Snapped**, render glides **monotonically forward** on every travel axis (no backward tile jump), lead
   bounded ≤ 3, and the run actually stepped (server step-seq ≥ 15). This is the UO5 guard: it should be
   GREEN on current code and RED on the UO5 code.
2. `GenuineReject_SnapsAndPullsRenderBack` — predictor oracle sees an open field but the server's grid
   blocks `x ≥ 205`; the prediction over-runs, the server holds at `x=204`. Asserts a reject **does**
   reconcile (Corrected+Snapped > 0), the server held at 204, and the reconcile **bounded** the over-run
   (final predicted-over-server lead in `[0, 4]`, vs ~17 unbounded) — correction still happens when it should.
3. `DroppedSnapshot_SelfHeals_NoPermanentDesync` — drops the snapshots at ticks 18–20 (a full confirm
   window). Compares the dropped run to a clean (no-drop) baseline run: identical final predicted tile +
   step-seq + server tile, zero Snapped, no backward render jump, lead bounded — the cumulative
   `RecipientStepSeq` re-syncs with no lasting trace.

Plus `LongStraightRun_WithJitter_NoCapNoSnap_StableLead` — the task's "+ a long straight run": 6 s east at
100 ms latency with ±15 ms jitter, zero Corrected/Snapped, bounded lead, monotonic glide.

### Out-of-scope slot
A clearly-marked comment block where the UO5 re-attempt's "frame-drop overshoot converges back" test slots
in against this same rig. NOT added — that convergence is the bug UO5 must fix, so it is RED on current code.

## Decisions / deviations
- **Client-driven faithfulness.** Modelled UoClientDriven as the real architecture: the predictor drives,
  each accepted predicted step emits a `StepCommitRequest` that reaches the server after uplink latency and
  is applied via `TryCommitStep` (with the real 0.5 anti-cheat floor), stamped with the server's current
  tick. An earlier draft committed every tick — that phase-shifted the server off the predictor and would
  have spuriously Corrected; rejected as a modeling artifact, not real behaviour.
- **Invariant 2 final-tile bound (not equality).** Reconcile bounds the over-run to `MaxInFlightLead` (2)
  of the confirmed tile, but the predictor can step ~1 more tile between the last snapshot and run-end, so I
  assert a small bounded lead (`[0,4]`), not exact equality to the wall tile. The load-bearing claim — the
  over-run is bounded, not ratcheting through the wall (~17 unbounded) — is what's asserted.
- **Invariant 3 via baseline comparison.** "No permanent desync" is asserted by comparing the dropped run's
  final state to a clean run's, rather than asserting predicted==server at an arbitrary run-end instant
  (the predictor legitimately leads the server by the in-flight count, so instantaneous equality is fragile).
- **Stack safety.** The accepted-steps buffer is heap-allocated once per run (not `stackalloc` in the frame
  loop, which would grow the stack across thousands of frames).

## Self-verification evidence
**BLOCKED — could not run any gate.** Both the `Bash` and `PowerShell` tools were **denied** this session
(the user is installing a network tool / may have a live session up). I therefore could **not** run
`run-checks.cmd`, `dotnet build`, `dotnet test`, or `git`. No commit was made and the todo file is not yet
deleted.

In lieu of execution I verified the harness by **hand-tracing the timing math against the current reverted
`LocalPlayerPredictor`/`WorldEntity` source** (full reasoning available on request):
- API surface checked against source: `PredictedTile`, `PredictedStepSeq`, `Sample`, `Reconcile`,
  `CalibrateToServerTick`, `SetClientDriven`, `Tick(now)` / `Tick(now, span, out)`; `WorldEntity.TryStep`,
  `TryCommitStep`, `StepSequence`; `TileGrid(int,int,IEnumerable<TileCoord>)`;
  `MovementCadence.EffectiveStepCadenceMs`. All match.
- **Invariant 1 reasoning:** `Reconcile` returns Matched ⟺ `(PredictedStepSeq − serverStepSeq) ≤ cap` (the
  re-projection of recorded same-direction steps reconstructs `confirmedTile + leadCount`, which equals the
  pre-anchor predicted head). The first `CalibrateToServerTick` re-seeds the predictor's tick frame onto the
  server's true phase (and shifts `_nextEligibleTick` by the same delta), so the predictor stays phase-locked
  to the server and the in-flight lead is `floor((t·50+lat)/150) − floor((t·50)/150) ∈ {0,1}` for lat ∈
  {50,100} — always ≤ `MaxInFlightLead` (2, server-paced) and ≤ `InFlightDirCapacity` (32, client-driven).
  ⇒ every steady-run reconcile is Matched ⇒ zero Corrected/Snapped, render untouched (monotonic). I expect
  GREEN.
- **Invariant 2:** open-oracle prediction over-runs; once `PredictedStepSeq − serverStepSeq > 2` the
  server-paced cap clamps the re-projection to `confirmed + 2`, which differs from the (further-ahead)
  predicted head ⇒ Corrected/Snapped fires and the head is bounded near the wall. I expect GREEN.
- **Invariant 3:** the predictor's stepping is driven by its own armed `_nextEligibleTick`, not by snapshots;
  the cumulative `RecipientStepSeq` on the post-drop snapshot re-anchors to the same trajectory ⇒ identical
  to the no-drop baseline. The `serverStepSeq < _highestReconciledStepSeq` stale-guard + the ±1-clamped
  calibration mean the drop leaves no lasting trace. I expect GREEN.

**Because I could not execute, these are reasoned expectations, not verified results.** The Orchestrator
must run `run-checks.cmd` (coordinating timing with any live session) to confirm.

## Known gaps / highest-risk areas
- **No execution.** The single biggest gap: every "pass status" above is a hand-trace, not a test run. If
  any invariant is actually RED on current code, the task says that is important signal — do NOT weaken the
  assertion; report it. The most likely surprise spots, in order:
  1. **Jitter reordering** in `LongStraightRun_WithJitter` — a reordered (older-seq) snapshot must hit the
     predictor's stale-guard and return Matched (not Corrected). I believe it does, but the lead/outcome
     interaction under reordering is the least-trivial path.
  2. **Invariant 2's `[0,4]` final-lead window** — if the predictor steps more than ~1 tile between the last
     snapshot and run-end, the upper bound may need a small widen. The qualitative claim (bounded, not ~17)
     is robust; the exact constant is the fragile part.
  3. **Client-driven first-commit timing** — the tick-0 step is taken pre-first-calibration; verify the
     commit/calibration interplay doesn't drop or double the first step.
- **Frame rate / float**: ~144 Hz frames on a 50 ms tick grid; tween sampling is float — the monotonic
  check uses a `1e-6` epsilon. Fine unless a reconcile retargets the tween backward (shouldn't, on Matched).

## What the reviewer should check
1. Run `run-checks.cmd` (hardened) — **coordinate timing**, the user may have a live server/client up; if it
   fails on a `Mmo.Shared.dll` lock, that's the running session, not this change.
2. Confirm all three invariants (32-case theory + the three facts + the long-run fact) are **GREEN** on
   current `review/tile-step-todo`. If any is RED, capture which case and the actual outcome counts — do not
   weaken the assertion.
3. Sanity-check the timing model against `MmoClient.Poll` / `ApplySnapshot` (the calibrate-then-reconcile
   order and the per-accepted-step commit emission) and against `WorldEntity.TryStep`/`TryCommitStep`.
4. If green, commit as one discrete revertable commit referencing TEST1 and delete
   `todo/TEST1-timing-faithful-reconcile-harness.md` in that same commit.
