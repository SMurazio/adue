# S82 — Fix the stuck-state latch: predictor runs away from the server during rapid turns and won't recover

Severity: movement correctness (stuck-state, easily reproduced). Residual after S81. **Reproduce
deterministically + confirm the root FIRST, then fix.** Client-core only.

## Repro (human, S79 overlay, S81 build) — DETERMINISTIC, easy
**Spamming down/left rapidly reliably triggers it** ("as soon as I try to make it happen it happens"). After
the trigger the predicted (green) marker sits ~1 tile off the server (magenta) and the gap **does NOT close
even standing still** — the prediction is latched off the server. At 0 latency a settled predictor must equal
the server, so this is a bug. It is NOT a rare jitter event — it's a deterministic consequence of rapid
alternation.

## The human's hypothesis (evaluate it directly — likely the root)
**"The server has to reconcile before the next prediction starts."** Concretely: the predictor's `Tick` runs
every frame (~16 ms) but `Reconcile` only lands on each snapshot (~50 ms, server tick rate). During rapid
input the predictor advances MULTIPLE steps between reconciles, so a per-turn misprediction **compounds**
across several un-reconciled predicted steps and runs away faster than the per-snapshot reconcile can pull it
back — and once the lead is large the calibration/estimate keeps re-creating it, so it never recovers. The
predictor's lead over the server's **confirmed** step-seq is effectively UNBOUNDED during a fast spam.

## Investigate (confirm/refute, with code refs)
- Trace the frame order: `MmoClient.Poll` does `PollEvents` (snapshots → `ApplySnapshot` → `Reconcile`) THEN
  `_predictor.Tick`. Confirm reconcile actually precedes predict, and how many predicted steps can accrue
  between two reconciles during rapid input.
- Is the predictor's lead (`PredictedStepSeq` − server `RecipientStepSeq`) bounded? During down/left spam does
  it grow well past the ~1 expected at 0 latency? That growth + non-recovery is the bug.
- Re-check the S81 calibration interaction (`EstimateTick` monotonic clamp `:578-588` ↔ `CalibrateToServerTick`
  `:233-271`): can a deterministically-induced forward estimate during spam latch the gate one tick ahead so
  the per-snapshot ±1 correction never catches up? (Earlier lead suspect — keep it on the list but the
  deterministic trigger says the dominant cause is the compounding above, not a rare spike.)
- Rule out `_moving` stuck true at rest, and the S77 stale guard wrongly ignoring recovery confirms.

## Fix direction (implementer chooses + justifies)
Make the prediction stay anchored to the reconciled server state — it must not be able to run away and latch:
- **Bound the lead:** the predictor may not advance `PredictedStepSeq` more than a small N beyond the latest
  confirmed server step-seq (N ≈ the in-flight amount; ~1-2 at 0 latency). When at the bound it HOLDS (waits
  for the next confirm) instead of predicting further — i.e. the human's "reconcile before the next prediction
  starts." This caps the divergence so a confirm can always pull it back.
- AND/OR fix the calibration so the estimate can fully re-converge to the server's authoritative tick (no
  permanent latch), preserving the no-per-frame-rewind / no-double-step intent.
- Must keep the S81 OffGridTurnParity sweep + spam-no-drift + calibration-jitter tests green and all S77
  reconcile tests green. Do NOT reintroduce per-frame backward rewind.

## Tests (the gate)
- **Deterministic stuck-state test (fails before, passes after):** drive the predictor + the REAL `WorldEntity`
  with a rapid down/left (or E/W) alternation that reproduces the runaway, then feed steady on-grid snapshots
  with the player STOPPED; assert the predicted tile re-converges to the server tile within a bounded number of
  snapshots AND that the lead never exceeds the bound during the spam. Today it must stay stuck.
- Keep S81 + S77 suites green.

## Constraints
- Client-core only; no server/protocol change. Server stopped (dev mode). Run
  `.\.shared\skills\mmo-dev\scripts\run-checks.cmd` before/after (try it; if Bash denied, note + continue —
  Orchestrator runs the gate + a LIVE overlay re-test: spam down/left, confirm the gap closes at rest). You
  can't run Godot. **Safe Local Execution** binds you. Do NOT commit, delete the task file, or push. If the
  root differs from the hypotheses above, STOP and surface it rather than guessing.

## Acceptance
- Root confirmed by a deterministic test (fails before, passes after); after any rapid-spam runaway the
  predictor re-converges to the server tile at rest within a bounded number of snapshots; the prediction lead
  is bounded; no per-frame rewind; `run-checks` green incl. all S77/S81 tests. Review-request →
  `review/review-request-s82-stuck-state-latch.md`. Do NOT commit or delete the task file.
