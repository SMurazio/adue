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

Source review: tile-stepped movement branch (`movement/tile-stepped`).
