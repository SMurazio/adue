# UO5 — lag-spike/frame-drop teleports the prediction forward and it NEVER reconciles (UO mode)

PRODUCTION on `review/tile-step-todo`. **HIGH priority — correctness/feel bug, reproduces easily at 50ms.**
User: a lag spike / frame drop made the avatar "teleport way forward and never reconciled" — the predicted
(green) tile sits far ahead of the confirmed (magenta) tile, static, permanently.

## Confirmed mechanism (verify, then fix)
1. On a frame drop, `LocalPlayerPredictor.Tick`'s catch-up loop banks up to `MaxTicksPerCall` (8) accepted steps
   in ONE call → the prediction jumps forward several tiles at once, and in UO mode emits a BURST of
   `StepCommitRequest`s (one per accepted step, up to `UoCommitBurstCap`).
2. **UO3 uncapped the in-flight re-projection for client-driven** (`_clientDriven` path: counts
   `PredictedStepSeq - serverStepSeq` and re-projects it ALL, NOT capped by `MaxInFlightLead=2`). The S83
   `Reconcile` comment is explicit the cap is load-bearing: *"without it… the gap grows unbounded."* UO3 removed
   it on purpose to hold for banked commits (fixing the release snap).
3. The server can't confirm a burst: `WorldEntity.TryCommitStep`'s cooldown gate + no-speedhack borrow accept ~one
   step per cooldown, so the rapid burst's later commits are REJECTED (the server's `RecipientStepSeq` doesn't
   advance for them). So the uncapped in-flight (the overshoot) NEVER drains → the prediction is stuck far ahead
   forever. That's the "never reconciles."

## Fix (investigate + choose; do NOT break the UO3 release fix)
The tension: UO3 must HOLD for genuinely-banked-and-will-confirm commits (the release case: a small, ~RTT-worth
in-flight that the server WILL accept) but must NOT hold an overshoot the server is REJECTING (the frame-drop
burst: large, rate-limit-rejected). Distinguish them. Candidate approaches (pick what the code/repro supports):
- **Latency-derived bound on the UO hold:** hold up to ~RTT-worth of in-flight (+ small margin), correct the
  excess beyond that toward the server. The release in-flight fits under it; a frame-drop burst exceeds it and is
  pulled back. (Replaces UO3's fully-uncapped re-projection with a dynamic cap, not the fixed 2.)
- **Stale/rejected-in-flight detection:** if `serverStepSeq` stops advancing toward `PredictedStepSeq` for N
  snapshots (the banked commits were rejected, not just in flight), reconcile DOWN instead of holding.
- **Don't over-bank in UO mode:** cap/space the catch-up burst so the client doesn't predict further than the
  server can validate at cadence (and/or have the server apply commits at their authored `ClientTick` so a
  cadence-spaced burst confirms — note this is the parked authored-tick-replay idea; only pull it in if needed).

Whatever you choose: a frame-drop overshoot must ALWAYS converge back to the server within a bounded time, AND a
normal release must still land smoothly without the backward snap UO3 fixed. Add predictor tests for BOTH: a
frame-drop burst converges back (no permanent overshoot); a normal release still holds-then-lands (no snap).

## Gates
- `run-checks.cmd` green + `godot-build.cmd` clean. **Do NOT run `stop-mmo`/gates that would kill a live session
  without flagging it** — if your shell can't gate cleanly, leave the work + review-request and the Orchestrator
  runs gates (coordinating timing with the user).

## Standing rules
One discrete revertable commit referencing this task; delete this file in that commit. **Safe Local Execution**.
You cannot run Godot — the human verifies the teleport/reconcile behavior live at 50ms.

## Acceptance
A lag spike / frame drop in UO mode no longer leaves the prediction permanently ahead — it converges back to the
server within a bounded time; the UO3 release behavior is preserved (no backward snap on a normal release). Gates
green.
