# S53 (redo) — Local-player prediction, the SIMPLE (UO/ClassicUO) way

Severity: feature (movement feel). Currently **flag-disabled** (`_predictionEnabled = false`, `MmoClient.cs`).
First attempt rubber-banded; root cause was **one mistake** (see below), not a hard problem — UO/ClassicUO
has done client-predicted, server-authoritative tile movement for decades. Keep it simple, like UO.

## The one real bug from attempt 1
The predicted local player was rendered **through the local `TileInterpolator`'s ~150 ms playout buffer** —
a buffer whose job is to render confirmed state *in the past* for jitter smoothing. Pushing "where I am
NOW" through a "show the past" buffer cancelled the snappiness, and feeding it backward correction tiles
made it oscillate. That's the rubber-band. **Everything else was overthinking** (trailing tolerances,
corner-cut blends, staircase corrections) — drop it.

## The model (UO-style — keep it this simple)
- **Predict:** on held intent (direction + moving), move the local player one tile per step cadence
  immediately, validating against the local blocked map (same rule the server uses). Render the local
  player **AT the predicted tile** with a plain per-step tween (old→new tile center over the step
  duration) — **NOT through the playout buffer.** This is the snappy part.
- **Reconcile = snap on divergence.** On each authoritative self-snapshot, if the server's confirmed tile
  disagrees with the prediction in a way the prediction can't be (server rejected a step / lag / teleport),
  **snap the predicted position to the server's** and carry on. Don't tween backward, don't tolerate — just
  resync, like UO. Divergence is rare because client and server share the map + rules; on LAN it's
  basically never in normal play.
- **Mouse (click-to-move) = keyboard.** Each step, pick the `Direction8` pointing toward the target and
  send `MoveIntent(dir, moving:true)`; stop on arrival. No A* path / waypoint machinery needed for v1 —
  a greedy heading toward the destination (re-picked each step) IS the keyboard input. (No wall-routing in
  v1; clicking across a wall stalls — acceptable, add routing later if it annoys.) Drive the heading off
  the **predicted** position so it re-aims against what the player sees.

## Scope / implementation
- Client only (`Mmo.Client.Core` + the Godot input). Local player ONLY; remote entities unchanged. No
  server/wire/AOI change. Harvest/interact stays on the **confirmed** tile (that part was right).
- The fix is mostly: stop routing the local predicted player through the playout-buffered interpolator —
  give the local player a direct present-time step-tween fed by the predictor, snap on reconcile. Re-enable
  `_predictionEnabled`.
- Replace/disable the S52 `PathDriver` waypoint-on-confirmed-tile path with the greedy-heading model above
  (or keep A* but drive its next-tile heading off the predicted tile — greedy is simpler, prefer it).

## The bar
- **Feel:** snappy press/release, click-to-move smooth, **no visible rubber-band** on keyboard OR mouse in
  normal LAN play. Human sign-off before shipping the flag on.
- **Tests:** predict-then-snap-on-divergence unit test; no-divergence steady state fires no snap; mouse
  heading picks the right `Direction8` toward a target. Keep it lean.
- **Revert criterion:** visible rubber-band → flag back off.
