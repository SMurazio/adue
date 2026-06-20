# S79 — F5 debug toggle: paint predicted vs server/confirmed tiles on the ground

Severity: dev tooling (diagnostic — directly supports catching the residual movement lag). A live **F5 visual-
panel** toggle that highlights, on the ground, the local player's **predicted tile** and **confirmed (server)
tile** in distinct colors, updated every frame. When prediction and server agree the two highlights sit on
the same tile; when the lag hits they separate — so the human can SEE the divergence in real time while
walking (which the autopilot capture isn't reproducing). Client-only; live toggle (per the
diagnostics-are-live-toggles guardrail) — no relaunch. Pairs with the S73 debug box (which shows the
continuous render position).

## What
1. Add an **F5 `CheckBox`** (e.g. **"Prediction tiles"**), backed by a `VisualTuning` flag
   (`DebugPredictionTiles`, default off), modeled on the existing F5 toggles (uncap-fps / frame-log / S73
   "Debug facing box") — applies live, no Apply.
2. When **on**, each frame highlight on the ground:
   - the local player's **predicted tile** (the predictor's logical tile) in one color (e.g. green),
   - the local player's **confirmed/server tile** in another (e.g. red/magenta — distinct from terrain).
   Use flat colored quads/markers at the tile centers (reuse the ground/wall mesh + `TileToWorld` approach in
   `MmoClientRoot`), sitting just above the ground so they're visible. Update positions every frame (both
   tiles move as the player walks). When **off**, draw nothing (zero overhead).
3. Optional (only if cheap): also paint a short fading TRAIL of the last ~6 predicted and confirmed tiles, so
   a transient lag leaves a visible streak. Core requirement is the two CURRENT tiles; the trail is a bonus.

## Integration points
- **Predicted tile:** the local predictor's logical tile. `LocalPlayerPredictor` already exposes
  `PredictedTile`. Surface it to the Godot layer (e.g. a `MmoClient.LocalPredictedTile` accessor that returns
  the predictor's `PredictedTile` when prediction is active, else `LocalTile`).
- **Confirmed/server tile:** `MmoClient.LocalTile` (already exists — the last snapshot's tile).
- **Render:** `MmoClientRoot` builds the ground/walls (`BuildZone`, `_wallMesh`/`TileToWorld`). Add a small
  highlight node-set (two markers, or a tiny pool for the optional trail) under the world root, repositioned
  each `_Process` frame from the two tiles when the flag is on; hidden when off. Keep it cheap (reuse a shared
  mesh + two materials).

## Constraints
- Client-only; no protocol/server/movement change. Live toggle (no relaunch); admin-gated like the rest of F5
  (note it). Default off = zero render change. Run `.\.shared\skills\mmo-dev\scripts\run-checks.cmd`
  before/after (try it; if Bash denied, note + continue — Orchestrator runs `godot-build`). If
  `GodotClientProjectTests` assert F5 panel contents, update them. You can't run Godot — Orchestrator runs
  `godot-build`; the human eyeballs the overlay. **Safe Local Execution** binds you. Do NOT commit or delete
  the task file.

## Acceptance
- `godot-build` green; F5 has a live "Prediction tiles" checkbox; on → the predicted tile and the
  confirmed/server tile are painted on the ground at the local player and track every frame (overlapping when
  in sync, separating under lag); off → nothing drawn. Review-request →
  `review/review-request-s79-prediction-tile-overlay.md`. Do NOT commit or delete the task file.
