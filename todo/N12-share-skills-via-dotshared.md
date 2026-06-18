# N12 — Share skills across agents via a `.shared/skills/` canonical folder + per-agent stubs

Severity: nit-tier infra (workflow improvement; **not gameplay-blocking**). Independent of the Godot
gameplay queue — can be done anytime, including before/in parallel with it. **User-requested.**

## Goal

One canonical copy of each skill, shared by both agents (Codex + Claude Code), instead of living only
under `.codex/`. Pattern (the user's, proven on another project): real skill in `.shared/skills/`,
thin **text stubs** in each agent's folder pointing to it.

## Mechanism: text stubs (NOT symlinks/junctions)

Text stubs are git-friendly, cross-platform, and need no admin/developer mode (Windows symlinks and
junctions are painful to version-control). Each agent folder gets a small `SKILL.md` with valid
frontmatter (name/description) whose body redirects to the canonical skill.

## Steps

1. Create `.shared/skills/mmo-dev/` and **move** the canonical `SKILL.md` + `scripts/` there from
   `.codex/skills/mmo-dev/`. (Note: `.shared/skills/mmo-dev/scripts` is the same depth below repo
   root as the old path, so each script's internal `$PSScriptRoot\..\..\..\..` repo-root resolution
   still works — verify, don't assume.)
2. Replace `.codex/skills/mmo-dev/SKILL.md` with a **stub**: frontmatter + body like
   "Canonical skill: `.shared/skills/mmo-dev/SKILL.md`. Scripts live at
   `.shared/skills/mmo-dev/scripts/`." (Keep the same `name`/`description` so discovery is unchanged.)
3. Create `.claude/skills/mmo-dev/SKILL.md` with the same stub, so Claude Code discovers it too.
4. **Update path references** from `.codex\skills\mmo-dev\scripts\...` to
   `.shared\skills\mmo-dev\scripts\...` everywhere: grep the repo (README.md, the canonical SKILL.md
   self-references, docs/*, any script that calls a sibling script, and the AGENTS.md `run-checks`
   path). Use `git grep -n "codex.skills.mmo-dev"` (and the backslash variant) to find them all.
5. Decide & document where NEW skills go: canonical in `.shared/skills/`, stub in each agent folder.

## Acceptance

- `.shared/skills/mmo-dev/` holds the only real copy (SKILL.md + scripts); `.codex` and `.claude`
  have stubs.
- Every documented script path resolves and runs: `run-checks.cmd`, `start-server.cmd`,
  `review-stress.cmd`, `godot-build.cmd`, `godot-run.cmd`, `stop-mmo.cmd`, etc., from the new
  location.
- `run-checks.cmd` green; no broken references (the grep from step 4 comes back clean except the
  stubs themselves).
