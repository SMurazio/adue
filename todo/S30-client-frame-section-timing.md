# S30 (T1) — Per-`_Process` section timing in the Godot client

Severity: should-fix. First piece of the client control/telemetry surface (see
`docs/client-control-telemetry-design.md`). Standalone value: it's the instrument to localize the
**residual frame hitch** (GC is now zero, so the occasional >16.7ms frame is NOT GC — we need to see
which part of the frame eats the time).

## What

In `src/Mmo.Client.Godot/MmoClientRoot.cs`, time each sub-step of `_Process` and surface the results:
- Sections: `poll` (`_client.Poll`), `renderState` (`SampleRenderStates`), `entities`
  (`UpdateEntities`), `camera` (`UpdateCamera`), `overlay` (`UpdateOverlay`). (Frame total already via
  `SampleFrameTiming`.)
- Use a cheap timer (`Godot.Time.GetTicksUsec()` or a reused `System.Diagnostics.Stopwatch`) — **no
  per-frame heap allocation** (the whole point is profiling; don't add churn; reuse fields, no boxing,
  no LINQ).
- Track per-section **last + max** (ms). Add a line to the F3 perf HUD, e.g.
  `proc poll/rs/ent/cam/ovl = a/b/c/d/e ms (max ...)`.
- Expose the per-section values via internal fields/accessors so T2 (telemetry channel) can read them.

## Why it's useful immediately

When a stutter happens, the section with the spike (or: all sections small but frame total large →
the time is in the engine/render/present, outside our `_Process`) tells us where the hitch lives.
This is the client analog of the server's `TickBudgetRecorder` that cracked the server issues.

## Acceptance

- F3 HUD shows per-section `_Process` timing (last + max).
- `godot-build.cmd` clean; `run-checks.cmd` green.
- No new per-frame allocations introduced by the timing (verify the gc counters don't regress).
- Do NOT commit — leave changes for Orchestrator review (the Orchestrator commits).
