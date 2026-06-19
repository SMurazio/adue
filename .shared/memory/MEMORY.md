# Shared Memory Index

This directory is the canonical, version-controlled memory store for the D:\MMO project. Both agents
read this index at session start, then read any note relevant to the current task.

- [Review handoff loop](review-handoff-loop.md) - two-agent loop: Orchestrator plans/reviews,
  Implementer writes code/tests, and review requests are verified independently.
- [Production-ready intent](production-ready-intent.md) - production readiness means open seams and
  reversible decisions, not building every future feature immediately.
- [Prefer scripts over MCP](prefer-scripts-over-mcp.md) - repeatable agent workflows should be
  deterministic scripts wrapped in shared skills.
- [Shared skills layout](shared-skills-layout.md) - canonical skills live under `.shared/skills/`
  with thin per-agent stubs.
- [Review findings to todo](review-findings-to-todo.md) - actionable review findings become
  `todo/` files.
- [Shared startup and memory layout](shared-startup-and-memory-layout.md) - canonical startup
  instructions and durable project memory live under `.shared/`, with root entry-point stubs.
- [Server tick performance](server-tick-performance.md) - the movement-slowdown saga (scheduler +
  GC, S21/S22), what "good" tick timing looks like, and why perf must be measured in Release.
- [Safe local execution](safe-local-execution.md) - run server/clients only via the skill scripts;
  never hidden-window / exec-bypass / PID-kill commands (triggers Defender). Binds both agents.
- [Orchestrator runs verification](orchestrator-runs-verification.md) - when Claude drives the loop via
  subagents: agents edit (accept-edits) but can't run scripts, so the Orchestrator runs all
  build/test/stress + commits; a targeted allowlist enables unattended runs; single shared tree (SDK is
  gitignored so worktrees can't build).
- [Content-not-state pivots](content-not-state-pivots.md) - static terrain ships as a seed the client
  regenerates (S42, not streamed — chunked S36/S36a abandoned); movement input is held-direction intent
  (S43, not a MoveStep stream — retired N21). Don't re-propose streaming them.

Claude Code may still auto-load notes from its user-level memory directory. That directory should
contain only a pointer note that directs the agent back to this versioned store.
