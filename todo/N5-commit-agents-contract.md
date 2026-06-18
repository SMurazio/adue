# N5 — Commit AGENTS.md so the shared handshake contract is durable

Severity: nit (process; do this on whatever branch is convenient)

## Problem

`AGENTS.md` (the agent collaboration contract / handshake point) exists in the working tree but is
**untracked** — it is not in any commit. It physically works today only because both agents share
the same `D:\MMO` checkout. A clean checkout, a different machine, or `git clean` would lose it, so
the contract is not actually durable in the repo.

## Fix

- Commit `AGENTS.md` to the repo so it is version-controlled and present in fresh checkouts for both
  agents.
- Confirm `todo/README.md` is tracked (it is) so the queue convention travels with the repo too.
- Do not change the contents as part of this task — content changes to AGENTS.md are an Orchestrator
  decision (per AGENTS.md itself).

## Acceptance

- `git ls-files AGENTS.md` returns the file.
- No behavior change; `run-checks.cmd` unaffected.
