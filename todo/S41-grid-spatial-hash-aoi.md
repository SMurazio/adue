# S41 — Grid / spatial-hash AOI (replace the naive O(N) per-client interest scan)

Severity: should-fix (scaling) — **now warranted, not just preventative** (see the S44 data below). The
measured optimization trigger. See `docs/capacity-ladder-study.md` (S40) and `docs/feature-roadmap.md`
Phase 7.

## Why (measured)

S40's capacity ladder showed the **AOI bucket is the only per-tick cost that grows** with population:
`budgetMs aoi avg` = 0.5 → 1.58 → 2.03 ms across 120 → 300 → 400 connected (Release, scattered). The
naive **per-client × all-entities distance scan** scales with the **entity count**, not just players.

**Reinforced by S44 (2026-06-19):** world-scattering ~1.3k resource-node entities pushed `aoi avg` from
**0.14 ms → 1.38 ms at only 120 clients** (Release, 1000²) — AOI became the **dominant tick cost**
(tickMs 0.29 → 1.59). Adding *content* entities (nodes now, NPCs/objects later) drives this as hard as
adding players, so the naive scan is no longer a future concern — it's the current bottleneck. gc still
0 and 1.59 ms is well within the 50 ms budget, but this is the thing to fix next on the scaling track.

## What

Replace the naive AOI candidate selection with a **spatial index** (uniform grid / spatial hash keyed by
tile→cell), so each client's interest query examines only entities in nearby cells instead of every
entity. **AOI semantics MUST stay identical** — same interest radius, same result set, same security
boundary (outside AOI ⇒ never serialized). This is a pure performance refactor, not a behavior change.

1. A spatial index over world entities (live in `WorldState`/`Zone`), updated as entities spawn/despawn/
   move (movement already has a single step path to hook). Cell size tuned to the interest radius
   (≈ one cell ≈ interest box) so a query touches a small fixed neighborhood (3×3 / 5×5 cells).
2. Rewrite the AOI candidate gather in `GameServer` (the `IsEntityInInterest` sweep used for snapshots
   AND for the S38 interact visibility check — keep BOTH paths on the same index so interaction and
   replication still agree) to query the index, then apply the exact same radius test.
3. Keep it allocation-light (reuse buffers) — the point is to cut tick cost, not add GC.

## Files (server only)
- `src/Mmo.Server/Runtime/` — spatial index structure; `WorldState`/`Zone` maintain it on
  spawn/despawn/move; `GameServer` AOI candidate gather + the interact visibility lookup use it.

## Acceptance
- **Parity test:** for randomized entity layouts, the grid-based AOI result set is byte-for-byte identical
  to the current naive scan (same entities, same AOI invariant). This is the critical correctness gate.
- The S38 interact AOI-visibility check still uses the same index (no divergence between "can replicate"
  and "can interact").
- **Measured win:** re-run the S40 ladder (incl. the dense 150-visible rung and the 300/400 scattered
  rungs); `budgetMs aoi avg` is flat or sub-linear vs the S40 numbers (no longer the dominant growing
  term). gc still 0; 0 errors. Capture before/after in a short note (or append to the S40 doc).
- `run-checks.cmd` green. No protocol/client change. Do NOT commit — Orchestrator reviews.

## Notes
- Behavior-preserving refactor — lean hard on the parity test; a subtle off-by-one in cell coverage that
  drops an edge entity is both a visible bug AND an anti-cheat hole.
- Independent of S36a (terrain) — different subsystem; can land in either order.
- After this lands, S40's dense-visible ladder can be pushed higher to set the next cap number.
