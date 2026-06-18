# N18 — On-screen performance stats HUD for the Godot client (FPS / frame / render / memory)

Severity: nice-to-have (dev tool). Complements S26 (watch the residual hitch tuning live) and builds
on the S25 frame instrumentation.

## Intent

A toggleable on-screen performance HUD in the Godot client, like the FPS/stats overlays games ship
with. Uses Godot's `Performance` singleton plus the .NET GC/frame data already collected in S25.

## Scope

Add to `MmoClientRoot` a perf HUD overlay showing, at minimum:

- **FPS** (`Engine.GetFramesPerSecond()` / `Performance.TIME_FPS`).
- **Frame time ms**: process (`Performance.TIME_PROCESS`) and physics (`Performance.TIME_PHYSICS_PROCESS`);
  reuse S25's frame ms/max and hitch count.
- **Render stats**: draw calls (`RENDER_TOTAL_DRAW_CALLS_IN_FRAME`), objects
  (`RENDER_TOTAL_OBJECTS_IN_FRAME`), primitives/vertices (`RENDER_TOTAL_PRIMITIVES_IN_FRAME`).
- **Memory**: video memory (`RENDER_VIDEO_MEM_USED` if available), static memory (`MEMORY_STATIC`),
  managed heap (`GC.GetTotalMemory(false)`), node count (`OBJECT_NODE_COUNT`).
- **GC**: the cumulative gc0/1/2 + frame-hitch count already tracked in S25.
- **"Graphic" element**: a small rolling **frame-time sparkline/graph** (e.g. a `Line2D` or a custom
  `_Draw` bar strip over the last ~120 frames) so spikes are visible at a glance, not just numbers.

## Decisions (adjust if the human wants otherwise)

- **Toggle with a hotkey (default `F3`)**, independent of `MMO_DEBUG_MOVEMENT`, so it's usable in
  normal play — not just debug runs. Fold the existing S25 `FRAME` line into this HUD.
- **No true CPU%** — Godot doesn't expose process CPU%; show process frame time (ms) as the proxy.
  A real CPU% via OS APIs can be a later follow-up if wanted.
- Keep it **allocation-light** (S25 lesson): throttle text refresh (~10Hz), reuse a `StringBuilder`/
  scratch buffers, and make sure the HUD itself does not introduce frame hitches.

## Acceptance

- A hotkey toggles a HUD showing FPS, frame ms, draw calls, object/primitive counts, memory, node
  count, GC counts, and a frame-time graph.
- Toggling on does not measurably add frame hitches (verify with the same FRAME instrumentation).
- `run-checks.cmd` + `godot-build.cmd` green.
