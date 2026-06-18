# N17 — Add a "log server output to file" option to the launch scripts

Severity: nice-to-have (closes the gap that led to a hand-rolled launcher).

## Why

Diagnostics sometimes need the server's stdout/trace in a **readable file** (e.g. reading
`tick_hitch` lines after a run). Today `start-server.cmd`/`start-godot-visual-check.cmd` open a
visible console window with no file log, which tempted a forbidden hidden-window redirected
`Start-Process` (tripped Defender — see `.shared/memory/safe-local-execution.md` and the Safe Local
Execution guardrail in `.shared/project.md`). Make the safe, script-based path do this so nobody
improvises a raw launcher.

## What

- Add an opt-in `-LogToFile` (and/or `-LogPath`) switch to `start-server.ps1` that **tees** server
  output to `.run/server.log` (+ `.run/server.err.log`) while keeping a **normal visible window**
  (no `-WindowStyle Hidden`). Use a benign approach (e.g. `Tee-Object`, or the server writing its own
  log file), not a hidden redirected process.
- Surface it through `start-server.cmd` and optionally `start-godot-visual-check.cmd`.
- Document in `docs/runbook.md` and `.shared/skills/mmo-dev/SKILL.md`.

## Acceptance

- `start-server.cmd -LogToFile` (or equivalent) runs a visible server AND writes `.run/server.log`.
- No script uses `-WindowStyle Hidden`, `-ExecutionPolicy Bypass`, or PID-kill one-liners.
- Does not trigger Defender; `stop-mmo.cmd` still stops it cleanly.
- `run-checks.cmd` green (no app behavior change).
