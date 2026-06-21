---
name: shared-skills-layout
description: "Skills live in canonical .shared/skills/ with a thin discovery stub under .claude/skills/."
metadata:
  node_type: memory
  type: feedback
---

Skills live in a canonical `.shared/skills/` folder, with a thin discovery stub under `.claude/`.

Pattern:

- The real skill lives in `.shared/skills/<name>/`.
- `.claude/skills/<name>/SKILL.md` is a thin text-stub with valid frontmatter and a pointer to the
  canonical skill.
- Use text stubs, not symlinks or junctions, so the layout is git-friendly and works on Windows.
- Refer to scripts by the shared path, for example
  `.\.shared\skills\mmo-dev\scripts\run-checks.cmd`.

This pairs with the prefer-scripts-over-MCP rule: lean, shared, low-overhead tooling belongs in the
repo.
