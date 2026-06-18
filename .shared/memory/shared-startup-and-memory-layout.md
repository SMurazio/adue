---
name: shared-startup-and-memory-layout
description: "Canonical project instructions and durable memory live under .shared; root agent files are stubs/imports."
metadata:
  node_type: memory
  type: feedback
---

The shared startup and memory layout mirrors the shared skills layout.

Canonical files:

- `.shared/project.md` is the project contract and startup checklist.
- `.shared/memory/` is the version-controlled durable memory store.
- `.shared/memory/MEMORY.md` is the memory index both agents read at session start.

Entry points:

- Root `AGENTS.md` is a Codex stub that points to `.shared/project.md`.
- Root `CLAUDE.md` is a Claude Code import stub that imports `.shared/project.md`.
- Claude Code user-level memory should keep a thin pointer back to `.shared/memory/`, because Claude
  auto-loads user memory but not repo-local memory.

Do not duplicate project rules across root agent files. Update the canonical shared file instead.
