# S84 — Reconcile the local player on EVERY snapshot (fix the at-rest latch)

Severity: movement correctness (the "static, gap won't close" latch). Builds on S83. Client-core only.

## Root cause (verified in the code)
`MmoClient.ApplySnapshot` (`src/Mmo.Client.Core/MmoClient.cs:533-550`) loops over the entities **present in the
snapshot** and only calls `entity.ApplySnapshot(...)` (→ predictor `CalibrateToServerTick` + `Reconcile`) for
those. The server uses **delta compression** — it re-sends an entity only while its `StateRevision` changes.
So while the player MOVES, their tile changes every step → they're in every snapshot → reconcile runs → the
prediction is corrected. The instant they STOP, they go idle → the server stops re-sending their tile (delta'd
out) → **the client stops reconciling the local player entirely** → any over-prediction left by a turn-spam
just sits there, uncorrected, forever. That is the "fine while moving, frozen-wrong the moment it's static"
symptom. S76 already rides `RecipientStepSeq` on EVERY snapshot header (real-delta AND keep-alive) for exactly
this, but the reconcile only consumes it when the tile happens to be re-sent.

## What
Make the local player reconcile **every snapshot**, even when it is delta'd out of the entity list:
- In `ApplySnapshot`, track whether the local entity (`LocalNetworkId`) appeared in the snapshot's entities.
- If it did NOT, and a predictor is attached, still run the local entity's calibrate+reconcile using its
  **last-known confirmed tile + facing** (unchanged — that's why it was delta'd out) plus the snapshot's
  header `RecipientStepSeq` and `ServerTick`. i.e. call the same calibrate+reconcile path with the entity's
  current `Tile`/`Facing` so `CalibrateToServerTick` keeps tracking the server clock and `Reconcile` re-anchors
  the prediction to truth while idle. Do NOT fabricate a tile change — the confirmed position is unchanged;
  only the reconcile/calibration must keep running.
- Keep the existing in-snapshot path unchanged (when the local player IS in the snapshot, it reconciles as
  today). Keep S83's authoritative reconcile + cap and S81's calibration intact.

## Tests (the gate — must model the DELTA, which prior tests did not)
- NEW test reproducing the latch: drive the predictor through a spam that leaves an over-prediction, then feed
  snapshots that DO NOT contain the local entity (delta'd out) but DO carry the header `RecipientStepSeq` +
  `ServerTick` with the player at rest; assert the predicted tile CONVERGES to the confirmed tile (today it
  stays stuck — fails-before; passes-after). If the existing test infra can't drive `MmoClient.ApplySnapshot`
  with an absent-local-entity snapshot, add the minimal harness or assert at the `MmoClient` seam — the test
  MUST exercise the delta'd-out routing, not just call `Reconcile` directly (calling Reconcile directly is the
  blind spot that hid this).
- Keep all S83/S81/S77 predictor tests green.

## Constraints
- Client-core only; no server/protocol change (the header seq already exists). Server stopped (dev mode). Run
  `.\.shared\skills\mmo-dev\scripts\run-checks.cmd` before/after (try it; if Bash denied, note + continue —
  Orchestrator runs the gate + a LIVE overlay re-test: spam down/left, STOP, the gap must close at rest). You
  can't run Godot. **Safe Local Execution** binds you. Do NOT commit, delete the task file, or push. If
  reconciling-every-snapshot causes any regression while MOVING (it should only affect the idle/delta'd-out
  case), STOP and surface it.

## Acceptance
- The delta'd-out convergence test fails-before/passes-after; the prediction re-anchors to the server at rest
  even when the local player is absent from the snapshot; no regression while moving; `run-checks` green incl.
  all S83/S81/S77 tests. Review-request → `review/review-request-s84-reconcile-deltad-out.md`. Do NOT commit or
  delete the task file.
