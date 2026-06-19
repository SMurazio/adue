# S53 (redo) — Local-player prediction, redesigned (the bolt-on rubber-banded)

Severity: feature (movement feel) — HIGH RISK, **DISABLED in code** (`_predictionEnabled = false`,
`MmoClient.cs`). The first attempt (commit 4b2fb72) shipped and **rubber-banded badly in play** — keyboard
noticeably, **mouse/click-to-move awful** — so it was flag-disabled (code + tests kept). Design:
`docs/movement-prediction-design.md` (update it with the lessons below before re-attempting).

## Why the first attempt failed (root causes — both integration, not the predictor class)
1. **Fights the playout-buffered interpolator.** The predictor fed predicted tiles into the LOCAL
   `TileInterpolator`, which has a deliberate ~1-step (150 ms) **playout delay** (renders confirmed state
   in the past for jitter smoothing) AND, on `Reconcile` corrections, gets fed **backward** confirmed
   tiles. Forward-predicted + delayed + backward-correction in one queue → oscillation = keyboard
   rubber-band. The predictor's own unit tests passed because they feed it ideal tiles; the bug is the
   `MmoClient` ↔ interpolator wiring.
2. **Click-to-move (S52) overshoots turns.** `PathDriver` advances its waypoint cursor on the
   **confirmed** tile (`LocalTile`), which now lags the prediction, so the predicted position runs
   straight past each turn before the path redirects → huge corrections at every corner = mouse "awful."

## Redesign requirements
- **Render the predicted local player at PRESENT time, separate from the buffered confirmed path.** The
  prediction IS now — don't push it through the ~150 ms playout buffer. Smoothly tween between predicted
  tiles with ~zero added delay; `Reconcile` corrects the predicted anchor directly (snap large / quick-
  blend small) and must **never queue a backward target into a playout buffer**.
- **Scope prediction to DIRECT held-direction (keyboard) input only.** **Exclude click-to-move** — it's a
  latency-tolerant "go there" command; leave its `PathDriver` advancing on the confirmed tile (its
  pre-prediction behavior, which was fine). This removes the worst interaction entirely. (Predicting a
  client-pathfound route + sending the turn intents without making the *server* cut corners is a separate,
  harder problem — out of scope for the redo.)
- **Reconcile correctly at the edges:** over-prediction on stop (client predicts ~1 step further than the
  server moves → gentle correct, not a yank), and direction changes (the in-flight tolerance is per the
  *current* held direction; a changed direction invalidates the old-direction in-flight line).
- Still local-player-only, server-authoritative, no wire/AOI/remote change. Harvest stays on the confirmed
  tile (that part was right).

## The bar
- **Feel:** snappy press/release with **NO visible rubber-band on keyboard** in normal play (mouse uses the
  non-predicted path, so it's unaffected). Human sign-off is mandatory before re-enabling the flag.
- **Tests:** keep the predictor unit tests; add an **integration-level** test of the `MmoClient` ↔
  interpolator path (predicted render advances; a correction blends/snaps without re-queuing a backward
  target). The unit-test-only coverage is what let the integration bug ship.
- **Revert criterion:** visible rubber-band → flag back off. Don't ship a worse feel than no-prediction.

## Note
No-prediction is a *legitimate* end state for a slow top-down sandbox (the design plan even says "probably
not ever"). Only pursue this if the walking feel is genuinely worth the netcode risk; otherwise stable
confirmed-state is acceptable and the flag stays off.
