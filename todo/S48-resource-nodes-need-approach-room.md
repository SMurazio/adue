# S48 — Scatter resource nodes with clear approach room (not jammed against walls)

Severity: should-fix (gameplay reachability). Play-test found a Tree at tile `(1,47)` — right against the
`X=0` border wall, so 3 of its 8 neighbours are wall; it felt un-harvestable until you stood *on* it.
Harvest logic is correct (Chebyshev ≤1, render==tile confirmed via the live client) — this is purely a
**placement** issue from S44's scatter, which drops nodes on *any* walkable tile including ones wedged
against walls/borders with no open approach.

## What

In `Zone.PlanResourceNodeScatter` (the S44 deterministic scatter), tighten the candidate acceptance: a
node tile must have **clear approach room**, not just be walkable itself.
- Primary rule: require the candidate's **8 neighbours to all be walkable** (so the node sits in open
  ground, reachable/approachable from every side). This keeps nodes off the border ring and away from
  interior wall segments.
- If that starves placement on a dense-obstacle map (too many rejections for the target count), relax to
  "≥ K walkable neighbours" (e.g. K=5, guaranteeing several approach tiles) — but the current map is
  mostly open, so all-8 should fill the target comfortably. Document the rule you pick.
- Keep everything else from S44 (deterministic seeded sampling, min-spacing, skip blocked, attempt
  budget). Placement stays deterministic.

## Files (server only)
- `src/Mmo.Server/Runtime/Zone.cs` — add the neighbour-walkability check in `PlanResourceNodeScatter`.

## Tests
- Scattered nodes all have walkable neighbours (no node tile is adjacent to a blocked/border tile) — on a
  map with the perimeter border + interior segments, assert every placed node has the required open
  neighbourhood.
- Determinism preserved (same seed → identical layout); count still ≈ the target; existing scatter +
  harvest/AOI tests pass.

## Acceptance
- No node spawns adjacent to a wall/border; every node is approachable from open ground (verify via the
  client / `client_entities` after a relaunch — node tiles sit away from walls).
- `run-checks.cmd` green. Server-only, no protocol/client change. Do NOT commit — Orchestrator reviews.
