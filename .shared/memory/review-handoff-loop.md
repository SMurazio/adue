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

Review cadence (2026-08-09 user decision — Adue): don't review every todo. `N-*` nits skip independent
review (orchestrator run-checks + regression test is the gate); `S-*` reviews **batch by shared code seam,
not by count** (one reviewer per cluster touching the same code — a focused diff catches more than a
grab-bag). The host-authoritative sim (run-loop/combat/damage/hit-test/protocol/two-session concurrency)
still gets one review per batch regardless: Adue ships a **bundled host-side server** (no dedicated box),
but the host still holds the game state both players trust, so a ready-race/desync/damage bug corrupts the
remote player over a flakier peer link — "authoritative" = who's the referee, not "is there a datacenter."
Narrowed vs the MMO: this is *sim correctness*, not dedicated-server ops (uptime/scale/AOI — parked);
couch co-op (one client, one clock) is lower-risk. **Why:** review value is concentrated in the referee
code, not uniform per todo; matching spend to that saves subscription usage without dropping the coverage
that matters. See [[session-and-model-economy]].
