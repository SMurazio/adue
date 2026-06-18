# N1 — Movement tick-budget bucket is always ~0 (misleading metric)

Severity: nit

## Problem

`GameServer.TickCore` opens an empty `using (tickBudget.Measure(TickBudgetCategory.Movement)) { }`
block (`src/Mmo.Server/Runtime/GameServer.cs:280`). Movement now happens on `MoveStep` receipt
during `PollEvents` (`HandleMessage` → `ClientSession.TryStep`, `GameServer.cs:194`), outside the
tick budget. So `budgetMs move=0.00` always, which is misleading.

## Fix

Either:
- Measure the actual step-handling cost (wrap `TryStep` dispatch in a `Movement` measurement that
  accumulates into the current tick's budget), or
- Remove the vestigial `Movement` bucket and drop it from the metrics label.

Pick whichever is simpler given how `TickBudgetRecorder` is structured; prefer keeping the bucket and
measuring real work if cheap.

## Acceptance

- `/metrics` no longer reports a constant `move=0.00` that misrepresents real cost (either it shows
  real movement cost, or the bucket is gone).
- `run-checks.cmd` green.
