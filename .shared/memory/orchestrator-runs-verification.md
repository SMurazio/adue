# Orchestration under Claude Code: agents edit, Orchestrator verifies

When the Orchestrator (Claude) drives the loop by spawning Implementer **subagents**, the Claude Code
permission model splits cleanly — and this split shapes how the loop actually runs:

- **Subagents can EDIT files** (accept-edits mode auto-approves their Edit/Write), so an Implementer
  agent writes code + tests fine in the background.
- **Subagents CANNOT run scripts/commands** — `run-checks.cmd`, `dotnet`, `review-stress`, any
  server/stress launch is permission-gated and gets **auto-denied** for a background agent (it can't
  surface an interactive approval). They correctly refuse to hand-roll around the denial.

**Therefore the working division is: Implementer agents implement + write tests + write the
review-request briefing; the Orchestrator runs ALL build/test/stress verification and commits.** This
is actually clean — single source of verification truth (the Orchestrator), visible/script-based, which
suits the [[safe-local-execution]] / company-managed-machine posture. Tell each Implementer prompt this
up front so it doesn't waste turns on denied script calls.

**Unattended operation:** add a *targeted allowlist* to `.claude/settings.local.json` (gitignored, never
committed) for exactly the Orchestrator's commands — `git` subcommands + the `.shared` skill scripts
(`run-checks`, `review-stress`, `stop-mmo`, `godot-build`). Then the implement→review→commit loop runs
with no human in the loop. Keep it tight — no blanket bypass. `review-stress.ps1` takes
`-SpawnDistribution/-WorldWidth/-WorldHeight` so dense/scattered/big-map stress is allowlistable without
env-prefixed launchers. Run commands WITHOUT a `cd` prefix (cwd is already the repo) so they match the
allowlist patterns.

**Single shared tree, not worktrees:** the repo-local SDK (`.tools/`) is gitignored, so `git worktree`
checkouts have no SDK and can't build — run Implementers sequentially in the main tree, Orchestrator
reviewing+committing between each (which is how the two-agent loop was designed anyway). Related:
[[review-handoff-loop]], [[prefer-scripts-over-mcp]].
