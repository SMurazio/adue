# S25 — Instrument and fix the client-side movement stutter (localized to the Godot client)

Severity: **should-fix, user-prioritized.** After S21/S22/S24 the server is proven clean, but the
human still perceives stutter. It is now localized to the Godot client.

## Evidence (server ruled out, client confirmed)

- Live repro, **Debug** server (what `start-mmo` runs) + **2 Godot clients**, `MMO_DEBUG_MOVEMENT=1`,
  duration+gap tick-hitch trace, full active movement session: **0 server tick_hitches.** The server
  is clean even in Debug with real clients.
- Closing one client and moving in a **single** client on a now-lighter machine: **still stutters.**
  So it is NOT machine/GPU contention from running two clients, and NOT the server.
- "Simultaneous on both clients" earlier was a red herring: both clients run on one machine, so a
  client/engine hitch freezes both at once.
- The Godot client has **no GC config** (`MmoClientGodot.csproj` → Workstation GC) and **no
  frame-timing/GC instrumentation**. `MmoClientRoot._Process` runs every frame:
  `SampleRenderStates → UpdateEntities → UpdateCamera → UpdateOverlay`. `UpdateEntities` sets
  `node.Position` and `label.Text` per entity per frame; `UpdateOverlay` builds status/metrics/chat
  strings per frame (likely per-frame allocations). Suspects: client GC (Gen0/Gen2 from per-frame
  garbage), interpolation jitter (`TileInterpolator`/`CopyRenderStatesTo`), or Godot engine frame
  pacing / VSync / per-frame `Label`/`Label3D` re-layout.

## Part 1 — instrument the client (do first; localize, don't fix blind)

Mirror the server's tick-hitch trace on the client:

- In `MmoClientRoot._Process`, measure frame `delta`; when it exceeds a threshold (absolute ms or a
  multiple of the target frame interval), log `mmo_trace side=client event=frame_hitch` with frame
  `durationMs`, client **GC deltas** (`GC.CollectionCount(0/1/2)`), and the interpolation
  `queueDepth`/`cadenceMs` (already exposed via `MovementDebugSnapshot`). Gate behind
  `MMO_DEBUG_MOVEMENT`.
- Optionally surface frame time + client GC counts in the on-screen overlay for live watching.
- Reproduce and classify: frame_hitch ↔ client `gc2`/`gc1` bump ⇒ **client GC**; ↔ `queueDepth`→0 /
  cadence jitter ⇒ **interpolation**; ↔ neither ⇒ **engine/VSync/render** (e.g. per-frame Label
  re-layout, mesh/shader).

## Part 2 — fix the localized cause (only after Part 1)

- **Client GC:** kill per-frame allocations on the render/overlay path (build overlay strings only
  when changed / throttle; avoid LINQ/temp lists per frame; reuse `StringBuilder`); only set
  `label.Text` when it changes; consider client **concurrent GC** / fewer allocations (client is
  latency-sensitive — prefer pause reduction over Server-GC throughput).
- **Interpolation:** tune `TileInterpolator` buffer/cadence for the Godot consumer.
- **Engine/VSync:** evaluate `DisplayServer`/project VSync + `Engine.MaxFps` / physics-vs-process
  frame pacing.
- Guardrails: measure with the Part 1 trace after EACH change; keep only changes that move the
  numbers; hot per-frame path only (not scene build / one-time setup).

## Acceptance

- Part 1: a client `frame_hitch` trace (+ optional overlay) that makes the cause visible; report the
  correlation for a reproduced stutter.
- Part 2: **human re-check** — single-client and 2-client Godot movement is smooth, no perceptible
  stutter; frame_hitch count drops; no client `gc2` during steady play.
- `run-checks.cmd` green.

Note: this is the client analogue of the server work in `.shared/memory/server-tick-performance.md`.
A reusable headless repro: run a Debug/Release server with `MMO_DEBUG_MOVEMENT=1` (logs to a file),
launch one Godot client, move, then read the client trace.
