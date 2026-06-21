---
name: review-handoff-loop
description: "Claude-only loop: orchestrator plans + verifies + commits; implementer subagents code; a fresh reviewer subagent verifies independently (author != reviewer)."
metadata:
  node_type: memory
  type: feedback
---

The project runs as a single-agent (Claude-only) loop with subagents — not two external agents, no human
relay.

The orchestrator (the main loop) plans, makes architectural/scope/protocol/priority decisions, populates
`todo/`, runs ALL build/test/stress verification, and makes the commits. It drives implementation by spawning
Implementer subagents, which edit code/tests but cannot run the gated scripts — see
[[orchestrator-runs-verification]].

An Implementer subagent's deliverable for a unit of work is a self-contained review request under
`review/review-request-<slug>.md`.

Review is independent by rule: the part of Claude that authored a change never solely certifies it. The
orchestrator commissions a FRESH reviewer subagent given only the live symptom + the diff (NOT the plan), which
re-runs checks/stress and re-reads the diff before the verdict. This is the "Review Independence" section of
`.shared/project.md`.
