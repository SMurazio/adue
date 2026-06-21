# CRASH1 — investigate intermittent server crash (live play)

PRODUCTION on `review/tile-step-todo`. **Priority: HIGH, but scheduled AFTER UO3/UO4 land** (user's call). User
reports the server "seems to be crashing" during live single-player testing (UO-mode / latency-sim sessions).
The 120c/30s stress gate is CLEAN (0 errors, 0 runtimeFaults) — but it runs the DEFAULT render path
(`CosmeticLead`), so it does NOT exercise the new `UoClientDriven` server code. A UO-mode-specific crash would
not show in stress. So the recent UO1 server surface is the #1 suspect.

## Prime suspects (recent, UO-mode-only, stress-unexercised)
- `GameServer.HandleMessage` new `case MovementModeMessage` handler.
- The per-step `StepCommitRequest` STREAM under UO mode (S103's `HandleStepCommit`/`TryCommitStep` were designed
  for ONE commit on release; UO mode fires ~7/s — an unhandled state, a null, an index, or the no-speedhack
  borrow math (`WorldEntity.TryCommitStep` lines ~333-336) underflowing/overflowing on rapid commits).
- The `ClientDrivenMovement` flag + the `StepHeldMovementIntents` guard interaction (e.g. a flagged session that
  disconnects mid-burst, or the shared `_moveSequence` cursor).
- Protocol v22 decode of `MovementModeMessage` on a malformed/partial packet.
Also consider non-UO causes (it may predate UO1): check the spike branch is NOT involved (it isn't on this
branch), and review recent commits.

## Approach
1. **Capture the actual crash** — exception type + stack + the last messages processed. The server is launched
   via `start-server.cmd`; per **Safe Local Execution**, if its output isn't already teed to a file, EXTEND the
   script (reviewed) to capture stdout/stderr to a log rather than improvising a raw launcher. Do NOT hand-roll a
   hidden/Bypass launcher.
2. **Reproduce** — run the server + one client, select `UoClientDriven`, set ~100ms latency, walk/spam
   directions + reversals + release repeatedly until it faults; note the trigger (time-based? action-based?
   on disconnect? on a specific reversal/release?).
3. Read the captured exception; trace to the handler; add a regression test that reproduces it headlessly if
   possible (e.g. feed the server a rapid commit stream / a disconnect mid-burst).
4. Fix as its own discrete commit; if the crash is in UO1 code, the fix references this task.

## Notes
- Get a real stack trace before theorizing — do not guess-patch.
- If the crash is an unhandled exception killing the whole server loop, also consider whether one bad session
  should be isolated (caught + that session kicked) vs crashing the process — but FIRST find the root cause.
- Ask the human for any specifics they saw (timing, on-screen error, what they were doing when it crashed).

## Acceptance
A captured stack trace, identified root cause, a fix (discrete commit), a regression test where feasible, and a
clean stress + a manual UO-mode soak that no longer faults.
