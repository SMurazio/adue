# S64 — Mouse heading from a continuous screen vector (fix prediction/sync struggle)

Severity: movement feel (mouse). Keyboard movement is good; **mouse** "struggles with prediction and sync."
Root cause (confirmed in `MmoClientRoot.CurrentMouseHeading`): the held heading is
`CursorHeading.FromTileDelta(predictedTile, cursorTile)` recomputed each frame — it uses the **integer
predicted tile** as origin (so it jumps a tile per step and shifts on every reconcile → prediction noise
feeds back into the heading) and **rounds the cursor to a tile** (so the 8-way octant flickers between
adjacent directions on tiny cursor moves, worst when the cursor is near the player). Client-only fix; no
server/protocol change (the server keeps receiving a clean held `Direction8`, just a stable one).

## What
1. **Compute the heading from a CONTINUOUS world vector**, not quantized tiles:
   `heading = nearest-of-8( cursorHitPointWorld - localPlayerRenderedPositionWorld )`, where
   - `cursorHitPointWorld` = the ray/ground-plane intersection (the existing `TryPickGroundTile` math) but
     **NOT rounded to a tile** — keep the continuous hit point.
   - `localPlayerRenderedPositionWorld` = the local player's **continuous rendered position** (the smooth
     predicted/tweened position the avatar is actually drawn at), NOT the integer predicted tile. Source it
     from the predictor/`MmoClient` (add a continuous `PredictedLocalPosition` in world units if one isn't
     exposed) or the local `PlayerVisual` transform — whichever is the position on screen.
2. **Dead-zone**: if the vector magnitude is below ~0.5–0.75 tile, emit no heading (hold previous / stop),
   so the heading doesn't whip around when the cursor sits on/near the player.
3. **Octant hysteresis**: require the cursor to cross an octant boundary by a small margin (a few degrees)
   before switching the held direction, so it doesn't flicker between two adjacent octants on the boundary.
   Track the last held octant to apply the stickiness.
4. Put the testable math in **`Mmo.Client.Core`** (extend `CursorHeading` with a
   `FromWorldVector(dx, dy, lastHeading, deadZone, hysteresis)` or similar) so the octant + dead-zone +
   hysteresis logic is **unit-testable headlessly**. `MmoClientRoot` just feeds it the two world positions.

## Constraints
- Client-only; no server/protocol change. Keep current input priority: **WASD > mouse > injected**; keep
  the "a deliberate mouse move clears injected/autopilot" behavior. Mouse stays hold-RIGHT-to-walk-toward-
  cursor (not click-to-destination).
- Don't regress the keyboard path (it's good). Don't couple the heading to the integer predicted tile at all.
- Add Core unit tests for `FromWorldVector`: correct octant per quadrant, dead-zone returns no/!changed
  heading, and hysteresis holds the previous octant within the margin (no flicker at the boundary).
- Run `.\.shared\skills\mmo-dev\scripts\run-checks.cmd` before/after. You can't run Godot — Orchestrator runs
  `godot-build`; the **human feels the mouse** (should track smoothly toward the cursor with no jitter/snap).
- **Safe Local Execution** binds you.

## Forks: surface, don't guess
If the local player's continuous rendered world position isn't cleanly available from Core, describe the
options (expose it from the predictor vs read the Godot `PlayerVisual`) rather than reintroducing a
tile-quantized origin.

## Acceptance
- `run-checks` green; `godot-build` green. Heading is derived from the continuous player-render→cursor world
  vector with a dead-zone + octant hysteresis; no dependence on the integer predicted tile; mouse tracks the
  cursor smoothly without the prediction/sync struggle (human check). Review-request →
  `review/review-request-s64-mouse-heading.md`. Do NOT commit; do NOT delete the task file.
