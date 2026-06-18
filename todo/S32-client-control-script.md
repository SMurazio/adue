# S32 (T3) — `client-control` skill script to drive the channel

Severity: should-fix. Depends on S31 (T2). See `docs/client-control-telemetry-design.md`.

## What

A skill script under `.shared/skills/mmo-dev/scripts/` (e.g. `client-control.cmd` + `.ps1`) that
connects to the client's localhost control channel and:
- sends a command sequence (args: e.g. `--autopilot=30s`, `--move=N --durationMs=...`, `--telemetry`),
- pulls `telemetry`/`interp`/`entities` and prints them,
- after an autopilot run, surfaces `.run/client-frames.csv` (and a quick summary: worst frames +
  which `_Process` section dominated them).

This is what lets the agent (and you) drive the client and read its state **without a human moving
the avatar** — the immediate unblock for profiling the residual hitch.

## Conventions

- Follow the existing script patterns (`.cmd` → `.ps1`, `-ExecutionPolicy RemoteSigned`, no hidden
  windows, no `Stop-Process -Id` sweeps — per `.shared/memory/safe-local-execution.md`).
- Document it in `.shared/skills/mmo-dev/SKILL.md` and `docs/runbook.md`.

## Acceptance

- `client-control.cmd --autopilot=20s` (against a running flag-enabled client) drives movement and
  prints a per-frame-timing summary identifying the worst frames + dominant section.
- Pure script; `run-checks.cmd` unaffected/green. Do NOT commit — leave for Orchestrator review.
