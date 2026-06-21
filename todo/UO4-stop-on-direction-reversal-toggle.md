# UO4 — "Stop on direction reversal" toggle (settle-then-go), to kill the left-right bounce

PRODUCTION on `review/tile-step-todo`. The rapid left-right (180° inversion) still bounces a bit. User wants:
on a direction inversion, **settle the character to a clean tile stop, then move the new way** — never reverse
mid-step. Latency-free (a client-side intent decision, not added input lag — consistent with the user's
"responsiveness over wobble" preference). Behind a **toggle** so it can be A/B'd.

## Behavior (confirmed with the user: "settle, then go new way")
When the held direction flips to the ~OPPOSITE (180°) of the current moving direction:
- Bring the avatar to a clean stop on a tile (settle to the current/next tile boundary) instead of immediately
  stepping the reversed direction mid-tween.
- Then resume moving in the NEW direction from that settled tile (the key is held, so movement continues; it just
  doesn't *reverse mid-step*).
So a 180° flip costs one clean settle, not a bounce. (Non-180° direction changes are unaffected — those already
step immediately per S98.)

## Where
Implement at the intent decision layer so it works for the active render mode (it matters most for the predictor
/ `UoClientDriven` + `Predicted` paths where the bounce lives). Likely in the client's intent handling where
`SetIntent` / `SendMoveIntent` are driven (detect the opposite-direction transition while moving), and/or the
predictor. Detect opposite via `Direction8` (the delta is negated). Do NOT add input latency — the settle is
immediate; it just suppresses the mid-step reverse.

## Toggle
Add an F6 checkbox "Stop on reversal" wired to a new `MmoClient` property (default OFF so current behavior is
unchanged until the user opts in). Make it contextual per UO2 (show it in the modes where it applies — the
predictor modes; hide/grey in modes where it's inert if applicable). Route it live (no restart).

## Gates
- `run-checks.cmd` green + `godot-build.cmd` clean. A unit test: a 180° flip while moving produces a settle/stop
  (not an immediate reversed step) when the toggle is on; with it off, behavior is unchanged.
- If your shell is denied, say so and do NOT claim green — the Orchestrator runs the gates.

## Standing rules
One discrete revertable commit referencing this task; delete this file in that commit. **Safe Local Execution**.
You cannot run Godot — the human verifies the feel live. This depends on / follows UO3 (same predictor/intent
area — do UO3 first to avoid churn).

## Acceptance
With "Stop on reversal" ON, rapid left-right settles cleanly (one stop) instead of bouncing, then resumes the new
direction; OFF = unchanged. Latency-free. Gates green.
