# S75 — Reject diagonal corner-cutting (stop slipping diagonally through wall corners)

Severity: movement correctness (you can walk through walls). **Server + client-predictor, kept in lock-step
parity.** Resolves the `// TODO: reject diagonal corner-cutting` in `WorldEntity.TryStep`.

## Problem
A diagonal step only checks the **destination** tile, not the two orthogonally-adjacent tiles it cuts
between. So moving diagonally past a wall corner (or between two blocked tiles) succeeds as long as the
landing tile is open — the player slips diagonally **through** walls. The human reports exactly this ("teleport
through it… the server thinking I can go through"). Both sides currently allow it (server
`WorldEntity.cs:195-197` checks `grid.IsWalkable(target)` only; the client predictor's walkability oracle
`MmoClient.IsWalkableForPrediction` is also target-only) — so it's *consistent* (no client/server desync from
this alone), but it's wrong: walls should be solid to diagonal movement through their corners.

## Fix — reject corner-cutting on BOTH sides, identically
When the step is **diagonal** (`delta.X != 0 && delta.Y != 0`), block it unless **both** orthogonal neighbours
are walkable: the tile at `(Tile.X + delta.X, Tile.Y)` AND `(Tile.X, Tile.Y + delta.Y)` — in addition to the
destination. If either side tile is blocked, the diagonal is rejected (treated like a blocked step → hold,
same as a blocked cardinal today). Cardinal steps are unchanged.
- **Server:** `src/Mmo.Server/Runtime/WorldEntity.cs` `TryStep` — replace the TODO; the diagonal rejection
  uses the same `grid.IsWalkable` for the two side tiles. Keep the existing blocked-step result/telemetry
  shape (it just now also triggers on a cut corner).
- **Client predictor:** the predictor's step must apply the SAME rule so prediction still mirrors the server.
  Today `LocalPlayerPredictor.Tick` calls `_isWalkable(target)` (single tile). Give the predictor the corner
  check too — cleanest is to pass a richer walkability check or have `Tick` test the two side tiles via the
  same `_isWalkable` oracle for diagonal targets. Whatever shape: predictor and server must reject EXACTLY
  the same diagonal steps.

## Parity is the whole point
- Extend the predictor↔server parity test (`LocalPlayerPredictorTests.TurnPathParity_*`, currently uses an
  EMPTY `TileGrid(64,64,[])` so it never tests walls): add a case with blocked tiles forming a corner, drive a
  diagonal into it, and assert `entity.Tile == predictor.PredictedTile` every tick (both hold). Also a pure
  server-side `WorldEntity` test: diagonal into a corner is rejected; diagonal through fully-open space still
  works; cardinal into a wall still holds.
- A wire/protocol change is NOT expected (the rule derives from the shared terrain both sides already have).
  If you find a reason one is needed, STOP and surface it (Orchestrator decision).

## Constraints
- Server + client-core change; no Godot change required (walls already render from `zone.BlockedTiles`). Run
  `.\.shared\skills\mmo-dev\scripts\run-checks.cmd` before/after (try it; if Bash denied, note + continue —
  Orchestrator runs gates + the live wall re-test). A running server can DLL-lock the build (`Mmo.Shared.dll`)
  — the Orchestrator will stop the server and run the gate. You can't run Godot. **Safe Local Execution** binds
  you. Do NOT commit, delete the task file, or push.

## Acceptance
- Diagonal corner-cutting rejected on server AND predictor identically; `run-checks` green incl. the extended
  parity test (corner case) + the new server-side corner tests; cardinal walls + open diagonals unaffected.
  Review-request → `review/review-request-s75-corner-cutting.md`. Do NOT commit or delete the task file.
