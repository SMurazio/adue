# Shared Project Instructions

This project is built by two AI agents working in step, coordinated by the human who relays messages
between them. This file is the canonical shared contract for both agents. Root `AGENTS.md` and
`CLAUDE.md` are entry-point stubs that point here. The `todo/` queue is the shared backlog; see
`todo/README.md`.

## Startup Checklist

At the start of every session:

1. Read this file.
2. Read `.shared/memory/MEMORY.md` and any memory note relevant to the task.
3. Read `todo/README.md` before working the queue.
4. Use `.shared/skills/` as the canonical repo-local skills location.

## Roles

**Orchestrator** (planner/reviewer, the Claude / architect agent)

- Plans the work and makes architectural and scope decisions.
- Writes paste-ready handoff prompts and populates the `todo/` queue.
- Maintains planning docs in `docs/`.
- Reviews the Implementer's output, verifying claims independently.
- Does not write or edit production code.

**Implementer** (the coding agent)

- Implements explicit handoffs and `todo/` items; writes code and tests.
- Does not make architectural, scope, protocol, or priority decisions unilaterally.
- Surfaces architectural forks, ambiguous specs, or disagreements instead of guessing.
- Emits a review-request briefing when a unit of work is done.
- Does not invent work outside the queue or an explicit handoff prompt.

## The Loop

1. **Plan** - Orchestrator produces a handoff prompt and/or `todo/` items.
2. **Implement** - Implementer works `todo/` in priority order (`S` before `N`): one commit per
   task, referencing the task filename; delete the task file in that same commit on success; a task
   that cannot be finished gets a `## Blocked` note and stays. Run
   `.\.shared\skills\mmo-dev\scripts\run-checks.cmd` before and after. New issues become new
   `todo/` files, never silent extra changes.
3. **Report** - Implementer writes a self-contained review-request briefing as
   `review/review-request-<slug>.md`. It must include intent, branch and base commit, how to diff,
   change manifest, decisions and deviations, self-verification evidence including a fresh
   120-client/60s stress run, known gaps, highest-risk areas, and what the reviewer should check.
4. **Review** - Orchestrator treats each file in `review/` as an inbound review task, independently
   re-runs build/tests/stress, re-reads the diff, produces a severity-ranked verdict, updates
   `todo/` with any new findings, and deletes the request file once reviewed.
5. Repeat.

The baton alternates. The Implementer waits for a plan or populated queue; the Orchestrator waits
for a review request.

## Decision Authority

- Architecture, scope, protocol, and priorities are the Orchestrator's call.
- Implementation details inside an accepted task are the Implementer's call.
- If the Implementer hits an architectural fork, an ambiguous spec, or a disagreement with the plan,
  it raises the issue in the briefing or a new `todo/` file rather than deciding unilaterally.

## Shared Artifacts

- `.shared/project.md` - canonical project contract and startup instructions.
- `.shared/memory/` - canonical, version-controlled durable project knowledge. Both agents read
  `.shared/memory/MEMORY.md` at session start.
- `.shared/skills/` - canonical repo-local skills and scripts.
- `AGENTS.md` - Codex entry-point stub that points to this file.
- `CLAUDE.md` - Claude Code entry-point import that points to this file.
- `todo/` - live backlog and source of truth for outstanding work.
- `review/` - Orchestrator's inbound review queue.
- `docs/` - plans and decision records.

## Project Guardrails

- Movement is tile-stepped (protocol v9), server-authoritative, and currently has no client
  prediction, lockstep, rollback, lag compensation, or LOS-for-AOI.
- Single process until metrics justify a split.
- Measure before optimizing.
- Use the repo-local SDK at `.tools\dotnet\dotnet.exe`.
- Use `.shared\skills\mmo-dev\scripts\run-checks.cmd` for the standard build/test check.
