# S56 — Mouse = hold-to-walk-toward-cursor (UO) + predictor mirrors server cooldown

Severity: should-fix (movement feel, follow-up to S53). Two play-test fixes:
1. **Mouse control is the wrong scheme.** S52/S53 built *click-a-destination* movement; the human wants the
   **UO control: hold the (right) mouse button and the character walks toward the cursor**, release to
   stop — identical to keyboard, direction taken from the cursor.
2. **Keyboard "change direction too quickly" snaps.** The predictor steps IMMEDIATELY on a direction
   change, but the server only steps once per cooldown regardless of direction — so rapid direction flips
   over-predict and snap back.

Client-only. No server/wire change.

## 1. Mouse = hold-to-move-toward-cursor
Replace the click-destination control (`ClickMoveController` toward a clicked target) with:
- **While the right mouse button is HELD:** each frame, ray the cursor to the ground plane → cursor tile;
  compute the `Direction8` from the player's (predicted) position toward the cursor tile (world-space tile
  delta, mapped to the nearest of 8); send `MoveIntent(dir, moving:true)` — exactly the keyboard path.
- **On release:** `MoveIntent(moving:false)`.
- No target tile, no arrival, no pathfinding, no waypoints — it's continuous "walk toward cursor," same as
  holding a WASD key. Re-aim every frame off the predicted position so it tracks the cursor live.
- WASD still works and takes/*shares* priority sensibly (keyboard overrides while a key is down).
- Keep `TilePathfinder`/`ClickMoveController` code if cheap, but it no longer drives movement — note what
  you did. (A "click once to path there" mode can come back later as a separate option.)

## 2. Predictor mirrors the server cooldown exactly
In `LocalPlayerPredictor`: the prediction must step on the SAME rule the server uses — step when a full
cadence has elapsed since the **last actual step**, NOT reset-to-immediate on a direction change.
- Track the last step time; the next step is due `lastStep + cadence`. A direction change updates the
  direction but does NOT bring the next step earlier.
- A genuine fresh start from idle is naturally immediate because the last step is long past (cadence
  already elapsed) — which also matches the server (its cooldown elapsed while idle). A quick stop→start
  must NOT double-step (respect the cadence since the last step), again matching the server
  (`WorldEntity.TryStep` only advances `_lastStepTick` on an accepted step and gates on it).
- Net: rapid direction changes no longer make the prediction out-step the server → no snap.

## Files
- `src/Mmo.Client.Core/LocalPlayerPredictor.cs` (cooldown rule), `MmoClientRoot.cs` (right-button-held
  input → MoveIntent toward cursor), possibly retire the `ClickMoveController` drive path.

## Tests
- Predictor: rapid direction changes within one cadence produce the SAME step count/timing as the server
  rule (no extra step on a direction flip); fresh-start-from-idle still steps promptly.
- Mouse heading: cursor-to-player tile delta maps to the correct `Direction8` across the 8 sectors.
- Existing predictor/movement tests pass.

## Acceptance
- Holding the right mouse button walks the character toward the cursor (steering live), release stops —
  feels like keyboard. Rapid keyboard direction changes no longer snap/rubber-band. `run-checks` +
  `godot-build` green; human feel sign-off. Revert criterion (rubber-band → `_predictionEnabled=false`)
  still stands.
