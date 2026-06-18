# Safe local execution: use the skill scripts, never malware-shaped commands

Binds BOTH agents. Run the MMO server and Godot clients only through the repo skill scripts under
`.shared/skills/mmo-dev/scripts/` (`start-server.cmd`, `start-godot-visual-check.cmd`,
`review-stress*.cmd`, `stop-mmo.cmd`, etc.). See the "Safe Local Execution" guardrail in
`.shared/project.md`.

**Forbidden:** ad-hoc shell that looks like a malware launcher on the user's Windows machine —
`Start-Process -WindowStyle Hidden`, `-ExecutionPolicy Bypass`, base64/escaped-quote one-liners,
`Stop-Process -Id` / `taskkill` PID killing. This pattern triggers Windows Defender and makes the PC
look attacked. It has already caused a Defender complaint and a silent "exit 255" launch failure.

**Why it happened:** wanting a Release server with output redirected to a readable file, the normal
scripts only open a visible console (no file log), so a hidden redirected `Start-Process` was
hand-rolled. Wrong call — bypassed trusted tooling and tripped Defender.

**How to apply:** keep launches visible and script-based; stop via `stop-mmo.cmd`; if a diagnostic
needs a capability the scripts lack (Release mode, server log teed to a file), extend the reviewed
script instead of improvising. If the only path looks like the forbidden pattern, stop and raise it.
Related: [[prefer-scripts-over-mcp]].
