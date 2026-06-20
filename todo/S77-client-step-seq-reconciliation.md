# S77 — Client server-reconciliation by step-sequence + replay (kills the rubberband)

Stage 2 of the per-step-ack plan (`C:\Users\stefano\.claude-mmo\plans\sleepy-tickling-lake.md`). Base: after
S76 (protocol v19 already emits `RecipientStepSeq`; `MmoClient.LastRecipientStepSeq` stashes it;
`WorldEntity.StepSequence` increments on accepted steps). **This is the behavior change.** Tests that verify it
ship IN THIS COMMIT (the reconcile/replay logic is subtle — do not land it unverified). Client-only.

## Why
A bare confirmed tile is ambiguous, so the current `Reconcile` `Corrected` branch
(`LocalPlayerPredictor.cs:304-312`) re-anchors the render BACKWARD onto a stale trailing confirm it can't
identify — the rubberband. Now that the snapshot carries the recipient's authoritative step-sequence, the
client can match a confirm to the exact predicted step and only correct on a TRUE divergence.

## What — LocalPlayerPredictor
1. **Step-seq + history.** Add `uint PredictedStepSeq` (exposed read-only for the parity test). Maintain a
   bounded history (ring ~32) of `(stepSeq → { tile, direction })` for the in-flight steps. Refactor the
   inner accepted-step body of `Tick` (the `_predictedTile = target` block, ~`:218-231`) into a private
   `AdvanceOneStep(direction, now)` that both `Tick` and replay call; it advances the tile, bumps
   `PredictedStepSeq`, and records `(PredictedStepSeq → tile, direction)` into history. (Turns/blocks do NOT
   bump the seq — mirrors the server.)
2. **New `Reconcile(TileCoord confirmedTile, uint serverStepSeq, TimeSpan now)`** (replaces the old
   `Reconcile(tile, now)`):
   - If `serverStepSeq` is older than the oldest history entry, OR the history's recorded tile at
     `serverStepSeq` equals `confirmedTile` → **Match**: the server agrees with what we predicted at that
     step. Discard history ≤ `serverStepSeq`. **Do NOT touch `_predictedTile`, the schedule, or the render.**
     Return `Matched`. (This is the common case and what removes the rubberband.)
   - Else (recorded tile at `serverStepSeq` ≠ `confirmedTile`) → **genuine misprediction**: re-anchor
     `_predictedTile = confirmedTile`, `PredictedStepSeq = serverStepSeq`; **REPLAY** by re-applying the
     recorded `direction` of each history step `serverStepSeq+1 .. (old)PredictedStepSeq` from the new anchor
     via `AdvanceOneStep` (the player's intent/direction is anchor-independent; re-running it from the
     corrected tile with the same walkability/turn rules recomputes the correct present tile). Then set the
     render: blend over one cadence if the present delta ≤ `SnapCorrectionThresholdTiles`, else snap. Return
     `Corrected` / `Snapped`.
3. **Remove** `_recentPath` / `RecordRecentPath` / `IsOnRecentPath` / `RecentPathCapacity` (S72) and the
   distance-as-identity guess. **Keep** `SnapCorrectionThresholdTiles` ONLY as the blend-vs-snap render choice
   after a positively-identified mismatch.
4. Idle/stop, turn-then-move, blocked-hold, cadence/turn-delay schedule (`_nextStepAt`/`_nextEligibleAt`)
   behavior is otherwise preserved. Remote-entity interpolation is **untouched** (local-player only).
5. On predictor (re)attach / respawn, seq resets to 0 and history clears.

## What — MmoClient
- Thread `RecipientStepSeq` to the predictor: `ApplySnapshot`/`EntityState.ApplySnapshot` must call the local
  entity's `Reconcile(tile, recipientStepSeq, now)` (the value is on the snapshot; pass it only on the
  predictor branch — remote entities still use the interpolator unchanged).

## Tests (in this commit)
- **Extend** `TurnPathParity_AgainstRealWorldEntity_*`: also assert `entity.StepSequence ==
  predictor.PredictedStepSeq` every tick (proves seq parity by the same construction as tile parity).
- **Benign trailing confirm → no render move**: predict 3 steps E (seq 3, (3,0)); `Reconcile((1,0), seq 1,
  now)` → `Matched`, `PredictedTile == (3,0)`, render sample unchanged. (Replaces the ring-based
  `StaleOldDirectionConfirm_OnRecentPath_IsBenign`, now deterministic via seq.)
- **Genuine misprediction → replay corrects**: predict E to (3,0) seq 3; the server says at seq 2 the tile is
  (2,1) (diverged); `Reconcile((2,1), seq 2, now)` → `Corrected`, re-anchored + replayed forward.
- **Blocked-hold-then-turn-along-wall**: hold into a wall (seq doesn't advance, server confirms held tile at
  the unchanged seq → `Matched`), then turn + slide along the wall; assert no backward render move.
- **Re-express against seq + keep green**: `ServerRejectsAStep_*` (rejection = seq mismatch → `Corrected`),
  `StartStopBoundary_*`, `OffPathConfirm_WhileMoving_*`, `ReversalThenStaleConfirm_*`. Update ALL existing
  reconcile-test call sites to the new `Reconcile(tile, seq, now)` signature. Delete tests that only asserted
  the now-removed ring internals.

## Constraints
- Client-core only; no server/protocol change (S76 already shipped the wire). Server stopped (dev mode). Run
  `.\.shared\skills\mmo-dev\scripts\run-checks.cmd` before/after (try it; if Bash denied, note + continue —
  Orchestrator runs the authoritative gate + a live MCP re-test with the S73 debug box). You can't run Godot.
  **Safe Local Execution** binds you. Do NOT commit, delete the task file, or push. If the replay can't be
  made parity-deterministic as specified, STOP and surface it.

## Acceptance
- `run-checks` green incl. the extended parity (seq), the benign-trailing (no render move), the
  misprediction-replay, and the blocked-hold-turn tests; the recent-path ring is gone; remote interpolation
  unchanged. Review-request → `review/review-request-s77-step-seq-reconcile.md`. Do NOT commit or delete the
  task file.
