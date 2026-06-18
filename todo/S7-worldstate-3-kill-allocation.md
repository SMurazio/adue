# S7 — WorldState Step 3: kill per-tick allocation (the GC-spike fix)

Severity: should-fix (perf/scaling). Plan: `docs/worldstate-zone-design.md` (Stage 2).
**Prerequisite: S6** (read paths go through `WorldState`).

## Goal

Make the replication step iterate `WorldState` with **reused buffers / pooled lists** instead of
allocating per tick. The current hot path uses per-tick LINQ (`Select` / `OrderBy` / `ToArray` /
`ToHashSet`) per recipient per tick, which is the source of the GC pauses behind the periodic tick
spikes (the residual ~32–53 ms `tickMs max` after S3 staggered the heartbeats). This is a **pure
performance change — no behavior change**.

## Approach

- Replace per-tick allocations in the snapshot/AOI build with reused scratch buffers owned by the
  replication step or `WorldState` (clear-and-reuse rather than allocate-per-tick).
- Avoid LINQ in the per-recipient hot loop; iterate the entity table directly into pooled buffers.
- Keep the output identical (same AOI selection, ordering, chunking, snapshot contents).

## Scope fence

- No SoA rewrite (still array-of-structs). No behavior/protocol change. No new gameplay.

## Acceptance

- `run-checks.cmd` green; behavior unchanged (all existing tests pass).
- A 120-client/60s stress run shows `tickMs max` **materially lower** than the current ~32–53 ms
  (target: the periodic GC spike largely gone; max much closer to the ~4–5 ms average). **Report
  before/after `tickMs avg/max` and the budget buckets.**
- This pairs with S3 (heartbeat stagger) to make the tick budget comfortable well past 120 clients.
