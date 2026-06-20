# S72 — Reconcile: recognize stale old-direction confirms as benign (kill the direction-change backward blend / "rubberband")

Severity: movement polish (prediction). Follow-up to S71 (which shipped Option B). **Client-only.**

## Background / diagnosis (from S71)
On a direction change, in-flight confirmations from the *old* direction keep arriving after the predictor
has turned and started stepping the *new* direction. `LocalPlayerPredictor.IsBehindOnPredictedLine` only
walks back along the **current** `_direction`, so a stale old-direction confirm misses it →
`Reconcile` falls into its correction branch (`LocalPlayerPredictor.cs:252-277`), which **re-anchors
`_predictedTile` onto the lagging confirm and blends the render *backward* toward it**. S71 Option B removed
the cadence *freeze* (the big multi-tile lag-then-jump), but the **re-anchor + backward blend remain** — a
small backward pull on direction changes. Directional capture (square autopilot, 90° corners) confirms a
residual ~0.045-tile backward wobble at ~0.9-tile divergence; the user perceives it as a "rubberband"
(possibly larger on the mouse-heading path, which can't be driven over MCP).

## What — Option A
Make `Reconcile` recognize a confirm that lies on the predictor's **recent path** (old OR new direction) as a
benign *trailing* confirm — return `Matched`, do **not** re-anchor and do **not** blend backward — so the
prediction keeps tracking forward through the reversal.
- Replace/augment `IsBehindOnPredictedLine` with a **recent-predicted-tile history**: keep a small bounded
  ring of the last ~6-8 tiles the predictor stepped onto (the lead + the pre-turn tiles). In `Reconcile`, if
  `confirmedTile` is in that recent path → `Matched` (benign trailing in-flight confirm). Only an **off-path**
  confirmedTile (genuine divergence/rejection/teleport) takes the correction branch.
- This subsumes the current "behind on the predicted line" check (current-direction in-flight tiles are in
  the recent path too) and adds the direction-change case.

## Parity / safety constraint (the reason S71 surfaced this as a fork)
Option A widens what counts as "server agrees" — the latitude that could let a genuine server **rejection**
during a direction change go uncorrected for an extra snapshot (the S56 guarantee). Keep it tight:
- The recent-path ring is **bounded** (small N) so a real desync can never be silently tolerated beyond it.
- A confirm **off** the recent path still corrects immediately (rejection / blocked step / teleport).
- **Preserve** the existing `ServerRejectsAStep_*` behaviour: a server rejection (the server put us somewhere
  NOT on our recent predicted path — e.g. a blocked step holds us back off-line) must still produce a
  `Corrected`/`Snapped` outcome, not be absorbed as benign. Re-read those tests; add a test asserting an
  off-path confirm still corrects.

## Tests
- The `Tick`-driven parity test stays green (this only touches `Reconcile`).
- **ADD** a test: drive E, flip to W, feed a stale **old-direction (east)** in-flight confirm via `Reconcile`
  and assert it returns `Matched` (no re-anchor — `PredictedTile` unchanged, no backward render move), then a
  genuine **off-path** confirm still returns `Corrected`/`Snapped`. (This is the seam S71 found untested.)
- Keep/adjust `StartStopBoundary_*` / `ServerRejectsAStep_*` — their reject-correction outcomes must remain.

## Constraints
- Client-only; no server/protocol change. Run `.\.shared\skills\mmo-dev\scripts\run-checks.cmd` before/after
  (try it; if Bash denied, note + continue — Orchestrator runs the gates + re-measures directionally via the
  S69/square capture after a relaunch). You can't run Godot. **Safe Local Execution** binds you.

## Acceptance
- `run-checks` green incl. predictor parity + the new benign-trailing-confirm test + the off-path-still-
  corrects test. On re-measure (square autopilot, signed render trajectory) the residual backward-motion
  frames drop to ~0. A genuine rejection still corrects. Review-request →
  `review/review-request-s72-reconcile-no-backward-blend.md`. Do NOT commit or delete the task file.
