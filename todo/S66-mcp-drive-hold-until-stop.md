# S66 — Fix MCP drive: `client_move` with no/0 duration must hold until stop

Severity: tooling bug. The MCP `client_move` tool contracts *"omit/0 keeps moving until client.stop"*, but
`MmoClientRoot.BeginManualMove` (IControlHost) sets `_injectedSingleStep = durationMs <= 0`, and that
one-frame "single step" is cancelled before the server's step cooldown elapses under the held-intent /
server-paced model (S43+) — so a no-duration drive produces **no step at all** (the avatar doesn't move).
Confirmed live: `client_move E` (no duration) didn't move; `client_move E durationMs=2000` moved 13 tiles.
This blocks Orchestrator/agent driving via the natural call.

## What
- In `BeginManualMove(direction, durationMs)`: `durationMs <= 0` ⇒ hold the intent **indefinitely** (until
  `StopMovement`), matching the tool contract; `durationMs > 0` ⇒ hold for the window (unchanged). Use a
  clean "indefinite" sentinel for `_injectedUntilSeconds` (e.g. `double.MaxValue`) so
  `CurrentInjectedDirection`'s expiry check never fires for a held move.
- Remove the now-dead single-step path cleanly: the `_injectedSingleStep` field, the one-shot clear branch in
  `SendHeldMovement` (`if (moving && injected.HasValue && _injectedSingleStep)`), and its resets in
  `StopMovement`, the autopilot setup, and the `CurrentMouseHeading` clear. Update the misleading comments
  (BeginManualMove + the field comment ~line 120).

## Constraints
- Client-only; no protocol/server change. Do NOT change WASD / mouse / autopilot behavior — only the injected
  (debug-channel) hold semantics. `client_stop` (StopMovement) must still halt.
- Run `.\.shared\skills\mmo-dev\scripts\run-checks.cmd` before/after (likely no Core change). You can't run
  Godot — Orchestrator runs `godot-build` and verifies via MCP `client_move` (no duration) → avatar holds
  moving until `client_stop` (needs a client relaunch on the new build; the Orchestrator will flag that).
- **Safe Local Execution** binds you.

## Acceptance
- `godot-build` green; MCP `client_move` with no/0 duration drives continuously until `client_stop`; timed
  moves + autopilot unchanged. Review-request → `review/review-request-s66-mcp-drive-hold.md`. Do NOT commit
  or delete the task file.
