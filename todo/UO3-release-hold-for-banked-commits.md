# UO3 — UoClientDriven release: hold for banked (committed) steps instead of snapping back

PRODUCTION on `review/tile-step-todo`. User-confirmed bug: in `UoClientDriven`, **releasing the move button snaps
the avatar BACKWARD** to the server's position. Root cause (confirmed with the user, verify in code): on release
(`!_moving`) the predictor's reconcile collapses `_predictedTile` onto the server's CURRENT confirmed tile, which
under latency is ~RTT (2-3 tiles) behind — because the at-rest reconcile treats "not moving" as "zero steps in
flight." But in UO mode there are **already-committed, already-requested steps the server hasn't confirmed yet**
(the RTT-worth of banked commits). The server WILL follow those banked requests; the client must WAIT for them,
not yank back.

This is why model B (CosmeticLead) feels graceful on release and UO does not: B banks nothing so it only ever
leads ≤1 tile (the same collapse is a tiny settle there, + S103 finishes the near-done step), whereas UO leads
the full RTT so the identical collapse becomes a big backward snap. The logic should be **basically the same** —
honor/finish the in-flight steps rather than collapse to the behind-server.

## Investigate first, then fix
Confirm the mechanism in `LocalPlayerPredictor.Reconcile` (src/Mmo.Client.Core/LocalPlayerPredictor.cs — the
`!_moving` in-flight handling, ~line 479, and the "converges EXACTLY onto the confirmed tile" path ~line 433) +
how `MovementRenderMode.UoClientDriven` routes through it. Determine exactly why the at-rest reconcile pulls
`_predictedTile` back to the (behind) confirmed tile while committed steps are still unconfirmed.

## Fix
On release in the client-driven path, the predictor must **hold the render at the predicted tile until the
already-committed steps are confirmed** (count the committed-but-unconfirmed steps as in-flight even when
`!_moving`), so the render settles forward onto the banked destination as the server confirms — NOT collapse onto
the server's current RTT-behind tile. Only a GENUINE server reject (a step the server actually refused) should
snap, and that snap is then small/correct. Net: releasing should land smoothly on the tile you walked to, like B,
because the server is just finishing the requests you already sent.

Keep it scoped to the client-driven/predictor path; do not regress `Predicted` (model A held-intent) or the
cosmetic modes. If the cleanest fix is to track a pending-commit count on the predictor (steps committed but not
yet confirmed via `RecipientStepSeq`) and hold while it's > 0 at rest, do that. If you find the real cause is the
server REJECTING the last banked step (the `CommitAcceptFraction=0.5` floor being too strict for a continuous
stream), raise it — the proper fix there is validating client-driven steps on the normal cooldown cadence
(accept any on-time step; the cooldown gate already caps rate), NOT weakening the S103 release floor. Pick the
cause the code actually shows and fix THAT; don't guess.

## Gates
- `run-checks.cmd` (hardened) green + `godot-build.cmd` clean. Predictor unit tests for the new at-rest-with-
  pending-commits behavior (no backward snap while commits pending; correct snap only on a real reject).
- If your shell is denied, say so and do NOT claim green — the Orchestrator runs the gates.

## Standing rules
One discrete revertable commit referencing this task; delete this file in that commit. **Safe Local Execution**.
You cannot run Godot — the human verifies the release feel live.

## Acceptance
Releasing in `UoClientDriven` at 100 ms lands smoothly on the tile you walked to (no backward snap), because the
render holds while the server confirms the banked steps; only a genuine reject snaps. Gates green.
