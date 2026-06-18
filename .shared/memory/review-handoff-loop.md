---
name: review-handoff-loop
description: "The project uses an Orchestrator/Implementer loop; roles are separate and review is independent."
metadata:
  node_type: memory
  type: feedback
---

The project is coordinated as a two-agent loop.

The Orchestrator plans, makes architectural decisions, writes handoffs, maintains `todo/`, and
reviews output. The Orchestrator does not write production code.

The Implementer writes code and tests for explicit handoffs and `todo/` items. The Implementer does
not make architecture, protocol, scope, or priority decisions independently. Ambiguities and new
issues are raised in a review request or captured as new `todo/` files.

The Implementer's final deliverable for a unit of work is a self-contained review request under
`review/review-request-<slug>.md`. The Orchestrator treats that briefing as a map to verify, not as
truth: re-run checks, re-run stress when relevant, and re-read the diff before deciding.
