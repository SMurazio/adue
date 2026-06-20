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
   standard-gate stress run (**120 clients / 30s** — fixed and comparable across tasks; longer 60s+
   runs are reserved for milestone/capacity studies, not per-task gating), known gaps, highest-risk
   areas, and what the reviewer should check.
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
- **Diagnostics are live, in-client toggles — not launch flags.** Any opt-in debug utility (frame-log
  CSV dump, uncap-FPS, motion/perf overlays, future tracing) is exposed as a runtime control — an F5
  visual-panel checkbox or a hotkey — that flips on/off **while the client is running**. Do NOT gate a
  diagnostic behind a launch-time env var or anything that needs a client or server restart. Minimize
  restarts: every avoided client/server restart tightens the debug loop. (Precedents: the F5 uncap-FPS
  checkbox; the F5 "Frame log (CSV)" toggle, S68.)

## Safe Local Execution (binds BOTH agents)

Run the server and clients **only** through the repo skill scripts under
`.shared\skills\mmo-dev\scripts\` (e.g. `start-server.cmd`, `start-godot-visual-check.cmd`,
`review-stress*.cmd`, `stop-mmo.cmd`). This is a hard rule, not a preference.

**Never** hand-roll ad-hoc shell that resembles a malware launcher on the user's machine. Concretely,
do NOT run commands that combine any of: `Start-Process -WindowStyle Hidden`, `-ExecutionPolicy
Bypass`, base64/escaped-quote-obfuscated one-liners, or `Stop-Process -Id`/`taskkill` PID-killing.
That pattern triggers Windows Defender (it has, including a silent launch failure) and makes the
machine look like it is under attack.

- Keep process launches **visible** (normal window) and **script-based**, never hidden/background.
- Stop processes via `stop-mmo.cmd`, not ad-hoc PID kills.
- If a diagnostic needs something the scripts don't do (e.g. a Release server, or server output teed
  to a file), **extend the script** (and have it reviewed) rather than improvising a raw launcher.
- If the only way to do a task looks like the forbidden pattern, stop and raise it instead of running
  it. See `.shared/memory/safe-local-execution.md`.
