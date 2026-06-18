---
name: shared-skills-layout
description: "Skills are shared across agents through .shared/skills/ with thin stubs in each agent folder."
metadata:
  node_type: memory
  type: feedback
---

Skills are shared between Codex and Claude Code through a canonical `.shared/skills/` folder.

Pattern:

- The real skill lives in `.shared/skills/<name>/`.
- Agent-specific folders such as `.codex/skills/<name>/` and `.claude/skills/<name>/` contain thin
  text-stub `SKILL.md` files with valid frontmatter and a pointer to the canonical skill.
- Use text stubs, not symlinks or junctions, so the layout is git-friendly and works on Windows.
- Refer to scripts by the shared path, for example
  `.\.shared\skills\mmo-dev\scripts\run-checks.cmd`.

This pairs with the prefer-scripts-over-MCP rule: lean, shared, low-overhead tooling belongs in the
repo.
