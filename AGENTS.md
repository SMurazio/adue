# Agent Collaboration Contract

This project is built by **two AI agents working in step**, coordinated by the human, who relays
messages between them. This file is the shared handshake point: both agents read it at the start of
work and honor it. The `todo/` queue is the shared backlog (see `todo/README.md`).

## Roles

**Orchestrator** (planner/reviewer — the "Claude" / architect agent)
- Plans the work and makes architectural & scope decisions.
- Writes the paste-ready handoff prompts that drive the Implementer.
- Maintains the `todo/` queue and the planning docs in `docs/`.
- Reviews the Implementer's output, verifying claims independently.
- Does **not** write or edit production code.

**Implementer** (the coding agent)
- Implements the plans and `todo/` items — writes code and tests.
- Does **not** make architectural or scope decisions unilaterally; surfaces forks instead of
  guessing (see Decision Authority).
- Emits a review-request briefing when a unit of work is done.
- Does **not** invent work outside the queue or an explicit handoff prompt.

## The Loop (how we work in step)

1. **Plan** — Orchestrator produces a handoff prompt and/or `todo/` items.
2. **Implement** — Implementer works `todo/` in priority order (`S` before `N`): one commit per
   task, referencing the task filename; delete the task file in that same commit on success; a task
   that can't be finished gets a `## Blocked` note and stays. Runs
   `.\.shared\skills\mmo-dev\scripts\run-checks.cmd` before and after. No scope creep — new issues
   become new `todo/` files, never silent changes.
3. **Report** — Implementer emits a self-contained **review-request briefing**: intent + branch &
   base commit, how to diff, change manifest, decisions & deviations, self-verification evidence
   (incl. a fresh 120-client/60s stress run), known gaps, highest-risk areas, and what the reviewer
   should check. It is written as `review/review-request-<slug>.md` (see `review/README.md`).
4. **Review** — Orchestrator picks up each file in `review/` as a task, verifies the briefing
   **independently** (re-runs build/tests/stress, re-reads the diff — never rubber-stamps), produces
   a severity-ranked verdict, updates the `todo/` queue with any new findings, and deletes the
   request file once reviewed.
5. Repeat.

The baton alternates between steps 1–2 (Orchestrator → Implementer) and 3–4 (Implementer →
Orchestrator). Neither agent runs ahead of the other: the Implementer waits for a plan/queue; the
Orchestrator waits for a briefing.

## Decision Authority

- **Architecture, scope, protocol, and priorities** are the Orchestrator's call.
- **Implementation details** are the Implementer's call.
- If the Implementer hits an architectural fork, an ambiguous spec, or disagrees with a plan, it
  **raises it** (in the briefing or a new `todo/` file) rather than deciding unilaterally.

## Shared Artifacts (the communication channel)

- `AGENTS.md` (this file) — the contract. Update only by agreement; changes are an Orchestrator
  decision.
- `todo/` — the live backlog and its convention (`todo/README.md`). The single source of "what's
  outstanding."
- `review/` — the Orchestrator's inbound review queue (`review/README.md`). The Implementer drops a
  `review-request-<slug>.md` here when work needs review; the Orchestrator processes and clears it.
- `docs/` — plans and decision records (e.g. `networking-design-plan.md`, `feature-roadmap.md`).
- `.shared/skills/` - canonical repo-local skills. Agent-specific skill folders under `.codex/`
  and `.claude/` should be thin stubs that point to the canonical skill.
- Handoff prompts (Orchestrator → Implementer) and review requests in `review/` (Implementer →
  Orchestrator), relayed by the human.

## Project Guardrails (already decided — do not relitigate)

- Movement is tile-stepped (protocol v9); server-authoritative; no client prediction/lockstep/
  rollback/lag-comp/LOS-for-AOID. See `docs/networking-design-plan.md`.
- Single process until metrics justify a split. Measure before optimizing.
- Use the repo-local SDK at `.tools\dotnet\dotnet.exe` and the `.shared/skills/mmo-dev` scripts.
