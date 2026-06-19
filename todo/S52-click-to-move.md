# S52 — Click-to-move (client-side pathing over the local map)

Severity: feature (input). Play-test: right-click-to-move is gone (it was the old web client / pre-pivot;
the Godot client only has WASD). Re-add it **client-only** — no server/protocol change — by pathfinding on
the locally-generated map and driving the existing held-direction `MoveIntent`. The server still validates
every step and stays authoritative.

## Why this is clean
Since S42 the client **regenerates the full blocked-tile map locally** (seed terrain). So the client can
A*-path on its own map and feed directions to the existing movement input. Entities don't block movement
(only walls do, and the client has the wall map), so a computed path stays valid for the session.

## What (Godot client + Core)
1. **Input:** right-click (or a configurable button) → ray/pick the clicked **tile** on the ground plane;
   ignore clicks on UI.
2. **Pathfind:** A* (or BFS — grid is uniform-cost, 8-way) from the local player's current tile to the
   clicked tile over the **local blocked map**. 8-way to match movement; respect the same
   diagonal/blocked rules the server uses (no corner-cutting if the server forbids it — match
   `TileGrid.IsWalkable` semantics). No path / unreachable → brief feedback toast, no move.
3. **Drive the existing intent:** walk the path by sending `MoveIntent(moving:true, dir)` toward the next
   path tile; advance to the next waypoint as the server **confirms** each step (watch confirmed tile, not
   prediction); send `MoveIntent(moving:false)` on arrival at the destination.
4. **Cancel/override:** any manual WASD input, or a new right-click, cancels/replaces the active path. A
   new right-click repaths from the current tile.
5. Keep the pathing logic in `Mmo.Client.Core` (testable, server-agnostic); Godot does the pick + wiring.

## Files
- `src/Mmo.Client.Core/` — a pathfinder over the blocked map + a "follow path → emit MoveIntent" driver
  (consumes confirmed-tile updates).
- `src/Mmo.Client.Godot/MmoClientRoot.cs` — right-click → tile pick → feed the driver; cancel on WASD.

## Tests (Core, headless)
- Pathfinder: open map straight line; around a wall; unreachable → empty/no-path; start==goal → no move.
- Path driver: given a path, emits the correct sequence of `MoveIntent` directions as confirmed tiles
  advance, and a final `moving:false` at the destination; a manual-input cancel stops emission.

## Acceptance
- Right-click a reachable tile → the character walks there along a sensible path (around walls), stopping
  on arrival; WASD still works and cancels an active path; unreachable click gives feedback, no move.
  No server/protocol change. `run-checks` green (Core); `godot-build` green; visual check by the human.
- Note (feel): with no prediction yet, the character still starts after server-confirm — pairs naturally
  with S53 (local-player prediction) once that lands, but does NOT depend on it.
