# S23 — Instrument and fix the residual client-side movement stutter

Severity: should-fix. **User-prioritized — after S21+S22 fixed the server, the human still perceives
stutter in the 2-client Godot view.** The server is proven healthy (S22: `gc=0/0/0`, ~2 ms avg tick,
a single ~22 ms OS blip/min in Release, even at 120 clients), so the residual stutter is almost
certainly **client-side or environmental — and we currently have no client-side instrumentation to
see it.** Localize before fixing; do NOT fix blind.

## Evidence / gap

- `src/Mmo.Client.Godot/MmoClientGodot.csproj` has **no GC config** → the Godot .NET client runs on
  Workstation GC (the same class of blocking-Gen2 pause we just fixed on the server).
- `MmoClientRoot._Process` does per-frame work (`SampleRenderStates`, `UpdateEntities`,
  `UpdateOverlay`) — `UpdateOverlay`/metrics string-building per frame is a likely per-frame
  allocation source (confirm by profiling, don't assume).
- `Mmo.Client.Core/ClientMovementTrace` records only `move_sent`/`tile_confirmed`/latency — it has
  **no frame-timing, no client GC, no per-frame hitch trace**. We are blind to client stutter.
- `MovementDebugSnapshot` already exposes `QueueDepth`/`EffectiveCadenceMs` from `TileInterpolator`,
  so interpolation starvation is observable if we surface it per frame.

## Part 1 — localize (instrument first)

Mirror the server's `ServerMovementTrace.TickHitch` on the client:

- In `_Process`, measure frame `delta`; when it exceeds a threshold (absolute ms or a multiple of the
  expected frame interval), log a `mmo_trace side=client event=frame_hitch` line with: frame
  `durationMs`, client **GC deltas** (`GC.CollectionCount(0/1/2)`), and the current interpolation
  `queueDepth`/`cadenceMs`. Gate behind `MMO_DEBUG_MOVEMENT` (or a dedicated flag).
- Surface client frame time + client GC counts in the existing overlay so a human can watch live.
- Reproduce the stutter and classify the cause:
  - frame_hitch correlates with a client **gc2/gc1** bump → **client GC**.
  - frame_hitch correlates with `queueDepth` hitting 0 / cadence jitter → **interpolation
    starvation / bursty snapshot delivery**.
  - neither → engine/VSync/frame-pacing or dev-box contention (exported build, fewer apps).

## Part 2 — fix the localized cause (only after Part 1 identifies it)

- **If client GC:** kill per-frame allocations on the render/overlay path (string building every
  frame, LINQ, temp lists) — cache/reuse `StringBuilder` and labels, throttle overlay refresh,
  reuse scratch collections. Extends N14's hotpath pass to the per-frame view path. Consider
  enabling **concurrent/background GC** on the client. NOTE: the client is latency-sensitive — prefer
  pause-reduction (fewer allocs + concurrent GC) over Server GC throughput tuning; justify whichever
  is chosen with before/after numbers.
- **If interpolation starvation:** tune `TileInterpolator` buffer/cadence for the Godot path (the web
  path was tuned in S13/S14; the Godot consumer may need its own tuning).
- Guardrails: measure with the Part 1 trace after EACH change; keep only changes that move the
  numbers. Hot per-frame path only — not cold setup/scene-build paths.

## Acceptance

- Part 1: a client `frame_hitch` trace + overlay that makes the stutter's cause visible; report the
  correlation (client GC vs interpolation vs environment) for a reproduced stutter.
- Part 2: **human re-check** — 2-client Godot movement is smooth, no perceptible stutter; frame_hitch
  count drops and no client `gc2` during steady play.
- `run-checks.cmd` green.

See `.shared/memory/server-tick-performance.md` for the server-side resolution this builds on.
