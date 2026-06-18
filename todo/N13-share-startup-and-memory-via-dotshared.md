# N13 — Share startup instructions + memory across agents via `.shared/`

Severity: nit-tier infra (workflow). **Not gameplay-blocking**; pairs with N12 (shared skills) — same
`.shared/` + per-agent-stub pattern. **User-requested.** Do N12 and N13 together if convenient.

## Part A — startup instructions (clean, like skills)

- Put canonical project instructions in `.shared/` (e.g. `.shared/project.md`, or a shared `AGENTS`
  doc). This is the single source for project rules / agent contract content.
- Root **`AGENTS.md`** (Codex) and **`CLAUDE.md`** (Claude Code — create it; none exists yet) become
  thin **stubs**: Claude Code can `@import` the canonical file (`@.shared/project.md`); Codex's
  AGENTS.md carries a text pointer to it. Keep the agent-collaboration contract (roles, the loop,
  todo/review conventions) in the canonical doc, referenced by both.
- Goal: no duplicated project rules across `AGENTS.md` / `CLAUDE.md`.

## Part B — shared memory (same idea, one tradeoff)

- Create `.shared/memory/` as the **canonical, version-controlled** store of durable project/agent
  knowledge. Migrate the current Claude memory notes into it (review-handoff-loop,
  production-ready-intent, prefer-scripts-over-mcp, shared-skills-layout, review-findings-to-todo,
  this layout) plus a `MEMORY.md` index.
- Both agents' startup docs (Part A) instruct: "read `.shared/memory/` at session start."
- **Tradeoff to accept:** Claude Code auto-loads memory from its user-level dir
  (`~/.claude/projects/D--MMO/memory/`), NOT the repo — so moving content to `.shared/memory/` loses
  auto-load. Mitigation: keep a thin **pointer** note in the Claude user-memory ("canonical project
  knowledge is in `.shared/memory/` — read it") so auto-recall still nudges me to load the shared
  store. Net: shared + versioned knowledge both agents use, with a pointer preserving the reminder.

## Acceptance

- Single canonical project-instructions doc + `.shared/memory/`; root `AGENTS.md`/`CLAUDE.md` are
  stubs/imports, not duplicated content.
- Both agents are instructed (via their startup docs) to read the shared memory.
- A Claude user-memory pointer to `.shared/memory/` exists so auto-recall still surfaces it.
- Nothing in the gameplay code/build changes; `run-checks.cmd` unaffected.
