# S70 — Launch 1 client by default; a separate script for 2

Severity: dev tooling (launch scripts, Safe-Local-Execution domain — extend the script, don't hand-roll a
launcher). Today `start-godot-visual-check` always launches **2** clients (GodotA + GodotB). Make the default
**1 client** (just GodotA, which carries the MCP control channel) and add a **separate script that launches
2** (the GodotA+GodotB remote-movement reference). Rationale: single-client is the common debug case and
avoids the "which window has the toggle / which one does the MCP drive" confusion.

## What
1. **`.shared\skills\mmo-dev\scripts\start-godot-visual-check.ps1`**: add a param **`[int]$Clients = 1`**
   (validate to 1 or 2). Launch the second `Start-GodotClient` (Index 2 / `$SecondName`) **only when
   `$Clients -ge 2`**. Everything else unchanged — stop-existing, start server, build, and the **control
   channel stays on Index 1 (GodotA)** so the MCP `mmo-client-control` still drives it. Make the trailing
   "second client visible / remote movement" verify-text conditional on `$Clients`.
2. **`start-godot-visual-check.cmd`** stays as-is (passes `%*`, so it now defaults to 1 client).
3. **New `start-godot-visual-check-2.cmd`**: a thin wrapper that invokes the same `.ps1` with `-Clients 2`
   (and forwards any extra args), mirroring the existing `.cmd`'s `powershell.exe -NoProfile -ExecutionPolicy
   RemoteSigned -File ... ` form. (No new `.ps1` — reuse the one script.)

## Constraints
- Tooling only; no production code / no movement change. Keep `RemoteSigned` (not Bypass), visible window,
  script-based — **Safe Local Execution** binds you; do NOT add hidden/bypass/PID-kill patterns.
- Don't change the server-start / build / stop behavior or the control-port logic — only gate the *second*
  client behind `$Clients -ge 2` and add the 2-client wrapper.
- You cannot launch anything (and must not) — the Orchestrator/human will test by running the scripts on the
  next relaunch. Run `run-checks` only if you touched .NET (you won't here) — this is scripts only.

## Acceptance
- `start-godot-visual-check.cmd` (or `.ps1` with no `-Clients`) launches exactly **one** client (GodotA, with
  the control channel); `start-godot-visual-check-2.cmd` launches **two** (GodotA+GodotB). Server start /
  build / stop unchanged. Review-request → `review/review-request-s70-single-client-launch.md`. Do NOT commit
  or delete the task file.
