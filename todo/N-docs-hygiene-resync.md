# N — docs-hygiene resync as an ongoing discipline (protocol/runbook/roadmap drift vs reality)

## Problem
The reference docs drifted ~10 wire versions behind the code before anyone noticed:
- `docs/protocol.md` stated "current shipped v25" (and v15 in the movement paragraph) while
  `ProtocolCodec.cs:37` is `Version = 35` — ten versions of combat/loot/living-enemies messages
  (v27 HP, v28-30 attack, v31 combat tuning, v32 damage events, v33 monster tuning, v34 spawner
  markers, v35 corpse loot) were entirely undocumented.
- `docs/runbook.md` Prerequisites said ".NET 8 SDK + Docker/Postgres", contradicting the
  repo-local-SDK guardrail (`.tools\dotnet`) and the Godot-4.7 (.NET) README.
- `docs/roadmap.md` drifted ("in flight: nothing since S63").

These were resynced in the docs-cleanup pass (branch `chore/cleanup-deadcode-docs`), but the drift is
RECURRING — `todo/README.md:36` already warned about it once (v12 vs v13) and it happened again at a
much wider gap. The cleanup fixes the symptom, not the cause.

## Fix
Make the docs resync a STANDING discipline, not a one-off:
- When a protocol change bumps `ProtocolCodec.Version`, update `docs/protocol.md` (the envelope version
  line + the message-set sections) in the SAME unit of work. This is already stated at `todo/README.md:36`
  — consider making it a checklist item in the implementer/review briefing template so it can't be skipped.
- Periodically (e.g. at each milestone) audit the "current/canonical vs stale" doc set: cross-check
  `protocol.md` version against `ProtocolCodec.Version`, `runbook.md` prereqs against the actual toolchain
  (repo-local SDK, Godot version), and `roadmap.md` against the shipped feature set.
- Consider a lightweight guard: a test or a `run-checks` step that asserts the version literal quoted in
  `docs/protocol.md` matches `ProtocolCodec.Version`, so the most common drift fails the gate.

## Acceptance criteria
- A mechanism exists (checklist line in the review template AND/OR a version-match assertion in the gate)
  that catches `docs/protocol.md` falling behind `ProtocolCodec.Version`.
- The "audit canonical-vs-stale docs at milestones" expectation is recorded somewhere durable
  (this file, a memory note, or the review template).
