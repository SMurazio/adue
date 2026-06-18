---
name: review-findings-to-todo
description: "After review, actionable findings become files in todo/ instead of living only in prose."
metadata:
  node_type: memory
  type: feedback
---

After a code review, actionable findings should be captured as files in the repo's `todo/` queue,
following `todo/README.md`.

Convention:

- One task per file, named `<S|N>-<slug>.md`.
- Each task contains the problem with file:line references where possible, the requested fix, and
  acceptance criteria.
- The Implementer works tasks in priority order, deletes each task file in the same commit that
  fixes it, and leaves blocked tasks in place with a `## Blocked` section.
- New issues found mid-work become new `todo/` files, never silent extra changes.

This keeps review output durable, auditable, and directly actionable. The `todo/` queue is the
source of truth for outstanding planned work.
