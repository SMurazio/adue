# S26 — Fix the Godot client render-stutter (Forward+ shader/pipeline compilation)

Severity: **should-fix, user-prioritized.** This is the confirmed cause of the residual movement
stutter after S21/S22/S24 (server) and S25 (client GC/instrumentation).

## Evidence (hard, from S25's instrumentation)

Live Debug server + Godot client, `MMO_DEBUG_MOVEMENT=1`, hitch threshold 18ms:

- Overlay: `FRAME ms=16.7/146.7 hitches=159 threshold=18.0 gc=6/1/0`.
  - **Not GC:** only 6 Gen0 / 1 Gen1 / **0 Gen2** collections over the whole session — cannot explain
    159 frame hitches; Gen2 (the pausing kind) never ran. S25's concurrent-GC/alloc work confirmed GC
    is a non-issue.
  - **Frame hitches on the client:** 159 frames > 18ms, worst 146.7ms.
- Server proven clean (S24: 0 tick_hitches with live clients). Single client on a light machine still
  stutters (rules out machine contention). The bursty `tile_confirmed` interpolation trace
  (134–300ms arrivals, growing queueDepth, render lagging ~2.5 tiles) is a **downstream symptom**:
  uneven frames → `Poll` runs unevenly → updates process in bursts.
- `project.godot`: `config/features=("4.6","Forward Plus")`, `rendering_device/driver.windows="d3d12"`.

Forward+ on D3D12 compiles shader/pipeline state objects lazily on first use of each
material/mesh/light combination → frame hitches when new entities appear and the camera reveals new
geometry. This matches the magnitude and recurrence observed.

## CONFIRMED (human test, renderer switch)

Switching `project.godot` to the Compatibility renderer (`renderer/rendering_method=gl_compatibility`
+ `.mobile`, features → "GL Compatibility") was tested live: client frame hitches dropped from
**159 → ~28-35** (both clients, consistent), `gc=0/0/0`, and the human reports movement is now
**smooth except occasional hitches** (was a constant stutter). This confirms Forward+ pipeline
compilation was the dominant cause. The change is currently in the working tree, **uncommitted** —
the Implementer should formalize it (commit) as part of this task.

Residual: ~30 hitches/session with a ~143ms max remain. Human reports they are **random, with
nothing new appearing on screen** — which argues against on-first-render shader/material compiles
(those track with new content). With `gc=0`, the likely residual causes are **VSync / frame pacing**
(Godot defaults VSync on; an occasional long frame waits a full refresh → doubled-frame hitch) and/or
**.NET tiered-JIT warm-up** (background re-compilation of hot methods in the first minute or two,
which settles). Distinguish via whether hitches taper off after ~1-2 min (JIT) or persist (VSync).

## Fix (measure each change with the FRAME overlay from S25)

Try in order, keeping what moves `hitches`/`max ms` down:

1. **Switch the renderer to Compatibility (or Mobile).** For a top-down 2.5D tile game this is almost
   certainly the right call and the biggest likely win — the Compatibility (OpenGL) backend does not
   have Forward+'s lazy pipeline-compilation stutter. Change `rendering/renderer/rendering_method`
   (+ `..._mobile`) and the `project.godot` features. Verify the scene still looks acceptable
   (lighting/shadows differ between backends).
2. **If Forward+ must stay:** enable shader/pipeline **precompilation / warm-up** (render or
   precompile the entity/wall/ground materials at load before gameplay), and **reuse shared
   meshes/materials** instead of allocating a new `CapsuleMesh`/`Material(...)` per entity in
   `CreateEntityNode` (a fresh material = a fresh pipeline to compile).
3. **Reduce per-frame render cost:** confirm shadows on the `DirectionalLight3D` aren't a hitch
   source (test with shadows off); check VSync / `Engine.MaxFps` / display frame pacing.

## Acceptance

- With S25's overlay: `hitches` count drops sharply and `max ms` is no longer in the tens-to-hundreds
  of ms during normal movement; capture before/after `FRAME` lines.
- **Human re-check:** single-client and 2-client Godot movement is smooth — no perceptible stutter.
  (This is the real acceptance; the saga has repeatedly passed metrics but failed the human check.)
- `run-checks.cmd` + `godot-build.cmd` green.

Quick human pre-test (optional): in the Godot editor, Project Settings → Rendering → Renderer →
"Compatibility", run a client, and see if the stutter disappears — that confirms the diagnosis before
committing the change.
