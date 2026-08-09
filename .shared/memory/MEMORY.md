# Shared Memory Index

> **FORK BANNER (2026-08-09):** this repo is **ADUE** — the standalone two-player co-op
> roguelite, forked full-history from the MMO repo at `10c0f9c`. Notes below this banner are
> **inherited MMO-era memory**: process notes (review loop, safe execution, skills layout)
> still bind; world/scale notes (ecology, AOI, persistence, stress capacity) describe systems
> that are PARKED here (prune on friction). New Adue-era notes go above the inherited list.

This directory is the canonical, version-controlled memory store for this project. Read this
index at session start, then read any note relevant to the current task.

- [Session and model economy](session-and-model-economy.md) — **main loop + all subagents =
  OPUS; Fable only for design or explicit user request**; one work-arc per session (idle gaps
  = premium cache re-writes); tail big outputs; don't restart with agents in flight.

- [Review handoff loop](review-handoff-loop.md) - Claude-only loop: orchestrator plans/verifies/commits,
  implementer subagents write code/tests, and a fresh reviewer subagent verifies independently.
- [Design decisions survive a Fable adversarial review](design-decisions-survive-fable-adversarial-review.md)
  — consequential design calls must pass a Fable RED-TEAM (prompted to refute, not bless) before being
  locked into docs/contract; scale to consequence; Law changes still a user decision.
- [Production-ready intent](production-ready-intent.md) - production readiness means open seams and
  reversible decisions, not building every future feature immediately.
- [Prefer scripts over MCP](prefer-scripts-over-mcp.md) - repeatable agent workflows should be
  deterministic scripts wrapped in shared skills.
- [Shared skills layout](shared-skills-layout.md) - canonical skills live under `.shared/skills/`
  with a thin stub under `.claude/skills/`.
- [Review findings to todo](review-findings-to-todo.md) - actionable review findings become
  `todo/` files.
- [Shared startup and memory layout](shared-startup-and-memory-layout.md) - canonical startup
  instructions and durable project memory live under `.shared/`, with a root `CLAUDE.md` import stub.
- [Server tick performance](server-tick-performance.md) - the movement-slowdown saga (scheduler +
  GC, S21/S22), what "good" tick timing looks like, and why perf must be measured in Release.
- [Safe local execution](safe-local-execution.md) - run server/clients only via the skill scripts;
  never hidden-window / exec-bypass / PID-kill commands (triggers Defender). Binds the orchestrator and every subagent.
- [Orchestrator runs verification](orchestrator-runs-verification.md) - when Claude drives the loop via
  subagents: agents edit (accept-edits) but can't run scripts, so the Orchestrator runs all
  build/test/stress + commits; a targeted allowlist enables unattended runs; single shared tree (SDK is
  gitignored so worktrees can't build).
- [Content-not-state pivots](content-not-state-pivots.md) - static terrain ships as a seed the client
  regenerates (S42, not streamed — chunked S36/S36a abandoned); movement input is held-direction intent
  (S43, not a MoveStep stream — retired N21). Don't re-propose streaming them.

Claude Code may still auto-load notes from its user-level memory directory. That directory should
contain only a pointer note that directs the agent back to this versioned store.
