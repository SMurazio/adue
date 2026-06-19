# TODO Queue

Each file here is one self-contained work item, produced from a code review. An agent works
through them and removes them as they land.

## Convention

- One task per file, named `<PRIORITY>-<slug>.md` where priority is `S` (should-fix, do first)
  or `N` (nit/follow-up). Work `S*` before `N*`.
- Each file states: the problem (with `file:line`), the fix, and acceptance criteria.
- On completion: implement the fix, add/adjust regression tests, run
  `.\.shared\skills\mmo-dev\scripts\run-checks.cmd`, then **delete the file in the same commit**.
  One commit per task; reference the task filename in the commit message.
- If a task cannot be completed, do **not** delete it — append a `## Blocked` section explaining
  why, and move on to the next.
- Do not expand scope beyond what a file describes. New issues discovered along the way become
  new `todo/` files, not silent extra changes.

## Current priority order (as of 2026-06-19)

`S` before `N` is the baseline, but several `S` items are live at once. Active order:

1. **Optimization / scaling track (do first):**
   - `S41` grid/spatial-hash AOI — **in progress**. Now *warranted, not preventative*: S44's scattered
     node entities made the naive AOI scan the dominant tick cost (0.14 → 1.38 ms at 120 clients).
   - `S36b` Godot per-chunk render+cull — depends on `S42`; client/visual.
2. **Feel-polish:** `S28` VSync/stutter (labelled nice-to-have despite the `S` prefix; needs a human).

**Done this session** (the gameplay loop + both correct-model pivots are validated):
- Gather loop: `S37` inventory, `S38` harvest verb, `S39` client UI, `S44` world-scattered nodes — playable.
- Terrain pivot: `S42` seed-based (ship the map, not the tiles; abandoned the chunked-streaming S36/S36a).
- Movement pivot: `S43` held-direction intent (retired `N21`); `S40` capacity study.

Dependencies: `S36b` needs `S42` (the map is local now); `S41` independent.

> **Protocol changes must update `docs/protocol.md`** (version + message list) in the same unit of work —
> it drifted to v12 while the wire reached v13; don't let it happen again.

Source review: tile-stepped movement branch (`movement/tile-stepped`).
