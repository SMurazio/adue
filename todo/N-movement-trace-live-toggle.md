# N — `MMO_DEBUG_MOVEMENT` violates the live-toggle guardrail (add an in-client checkbox)

## Problem
The MOVE-trace diagnostic is gated by an **environment variable read once at launch**, with **no live
in-client toggle** to flip it while the client runs — a direct violation of the project's live-toggle
guardrail ("Diagnostics are live, in-client toggles — not launch flags ... do NOT gate a diagnostic
behind a launch-time env var or anything that needs a client or server restart").

- `ClientMovementTrace.FromEnvironment()` seeds `Enabled` from `MMO_DEBUG_MOVEMENT` ONCE at startup
  (`src/Mmo.Client.Core/ClientMovementTrace.cs:20-23`); `Enabled` is get-only.
- Consumed at `MmoClient.cs:~260`, gated in the Godot overlay at `MmoClientRoot.cs:~2086`.
- To turn the trace on/off you must restart the client with a different env var — exactly the
  restart-the-client anti-pattern the guardrail forbids.

Contrast: `MMO_DEBUG_FRAME_LOG` (F5 "Frame log (CSV)" checkbox, S68) and `MMO_UNCAP_FPS` (F5 uncap-FPS
checkbox) BOTH also expose a live F-panel checkbox. The MOVE-trace is the odd one out.

This is a FIX, not a deletion — the trace code works; it just lacks a runtime control.

## Fix
Expose the MOVE-trace as a **live in-client toggle** (an F1/F3 visual-panel checkbox or a hotkey),
matching the existing frame-log / uncap-FPS checkboxes:
- Make `ClientMovementTrace.Enabled` settable at runtime (or add a `SetEnabled(bool)` / mutable flag)
  so flipping the checkbox turns the console trace on/off **without a restart**. The env var can remain
  as the initial seed (like the frame-log default) but must NOT be the only control.
- Add the checkbox to the appropriate F-panel (F3 perf or F1 visual), wired like `_frameCsvCheck` /
  `_uncapFpsCheck` — added to the UI + toggled live, its pressed state reflecting current trace state.
- Keep the unconditional snapshot tracking as-is (live HUDs already read it without the console gate);
  only the console-output gate flips.

## Acceptance criteria
- The MOVE-trace can be turned on and off **while the Godot client is running**, with no client restart
  and no launch-flag dependency.
- The control is a visible in-client checkbox/hotkey consistent with the frame-log/uncap-FPS toggles.
- Existing behaviour (env var as initial default) still works; the trace output is unchanged when on.
