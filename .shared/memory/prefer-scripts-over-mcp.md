---
name: prefer-scripts-over-mcp
description: "For repeatable processes, prefer deterministic scripts wrapped in shared skills over token-heavy MCP servers."
metadata:
  node_type: memory
  type: feedback
---

The user wants repeatable tooling done as deterministic scripts wrapped in a skill, not as MCP
servers.

Why: an MCP injects tool schemas into context every turn and adds per-call overhead for processes
that are usually fixed and repeatable. A script has no standing context cost, is auditable in git,
and is easy to run.

How to apply: when a repeatable capability is needed, add or update a small `.cmd` or `.ps1` under
`.shared/skills/mmo-dev/scripts/` and document it in `.shared/skills/mmo-dev/SKILL.md`. Reserve MCPs
for genuinely interactive or stateful capabilities that a fixed script cannot express.
