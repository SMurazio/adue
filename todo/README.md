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

1. **Correct-model + optimization track (do first):**
   - `S43` held-direction movement intent — replaces the redundant per-tick `MoveStep` streaming with
     input-as-intent (retires `N21`). Needs a human feel-check. See `docs/movement-input-model.md`.
   - `S41` grid/spatial-hash AOI — the measured AOI-scan cost (preventative; ~2 ms at 400 clients).
   - `S36b` Godot per-chunk render+cull — depends on `S42`; client/visual.
   - *Done:* `S42` seed-based terrain (ship the map, not the tiles; replaced the abandoned S36a streaming).
   Gates from `docs/capacity-ladder-study.md` + the 2026-06-19 terrain & movement model decisions.
2. **Gameplay UI:** `S39` gather client UI (makes the S37/S38 loop playable). Deprioritized per direction.
3. **Feel-polish:** `S28` VSync/stutter (labelled nice-to-have despite the `S` prefix; needs a human).

Dependencies: `S36b` needs `S42` (the map is local now); `S39` needs S38 (done); `S41`/`S43` independent.

> **Protocol changes must update `docs/protocol.md`** (version + message list) in the same unit of work —
> it drifted to v12 while the wire reached v13; don't let it happen again.

Source review: tile-stepped movement branch (`movement/tile-stepped`).
