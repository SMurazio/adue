# S27 — Chase the residual client frame hitches (post-Compatibility)

Severity: should-fix (polish). The core movement stutter is fixed (S26: Forward+ → Compatibility,
159 → ~30 frame hitches, human confirms "smooth except it hitches"). This task chases the remaining
~30 random hitches/session so movement is fully smooth.

## What we know

- Renderer switched to Compatibility (S26) — dominant cause gone.
- Residual: ~28-35 frame hitches/session, `max ~143ms`, `gc=0`, and the human reports they are
  **random with nothing new on screen** (so NOT on-first-render shader/material compiles, which would
  track with new content).
- S26 only formalized the renderer switch; it did **not** address the residual. The likely causes
  (from the random/no-content pattern + gc=0) are **VSync / frame pacing** and/or **.NET tiered-JIT
  warm-up**.
- N18 now gives an F3 perf HUD with FPS, frame ms, draw calls, memory, GC counts, and a rolling
  frame-time graph — use it to diagnose live.

## F3 HUD evidence (decisive)

Live reading from the N18 HUD during stutter (stutter occurs on BOTH monitors — earlier
monitor-refresh idea was wrong):

```
fps 60.0
frame ms last/max 16.7/148.9
process/physics ms 16.8/0.1
draw/objects 129/2888
primitives 10144
managed MB 4.8     gc 5/1/0     nodes 563     hitches 53 (>18ms)
```

Interpretation:
- **VSync-locked at 60 fps** (frame 16.7ms). With VSync on, a frame that exceeds 16.7ms misses the
  refresh and waits a full extra frame → doubled frame → visible stutter.
- **No frame headroom:** `process ms ~16.8` is right at the 60fps budget, and the scene renders
  **2888 objects / 129 draw calls** — far too many for a tile map. Almost certainly the walls are
  individual `MeshInstance3D` nodes (one per blocked tile; `BuildZone` adds a wall mesh per tile).
- **Not GC** (`gc=5/1/0`, managed 4.8MB) — confirmed again.

Root cause: heavy per-frame render load (no headroom) + VSync at 60 turning occasional over-budget
frames into hard hitches.

## Plan

1. **Cut per-frame render load (primary).** `BuildZone` creates one `MeshInstance3D` per blocked
   wall tile (and a ground + grid), driving ~2888 rendered objects / 129 draw calls. Batch the
   static geometry: use a `MultiMeshInstance3D` for the walls (and merge ground/grid), so the wall
   field is a handful of draw calls instead of hundreds of objects. Goal: `draw/objects` and
   `process ms` drop sharply → frames sit well under the 16.7ms budget with headroom to spare.
2. **VSync mode (complementary).** Once there's headroom, test `display/window/vsync/vsync_mode`
   (Enabled vs Adaptive vs Mailbox) and/or `Engine.MaxFps`, so a stray long frame degrades gracefully
   instead of waiting a full refresh. Measure on a high-refresh and a 60Hz monitor.
3. **Per-entity mesh/material reuse (secondary).** `CreateEntityNode` allocates a new `CapsuleMesh` +
   `Material(...)` per entity; reuse shared resources so entity spawn doesn't add objects/compiles.
4. Measure each change against the N18 HUD (`draw/objects`, `process ms`, `hitches`, `frame max`);
   keep what lowers them.

## Acceptance

- N18 HUD: `hitches` count materially lower and no recurring mid-play `max ms` spikes during steady
  movement; report before/after.
- **Human re-check:** 1- and 2-client Godot movement is smooth with no perceptible hitching.
- `run-checks.cmd` + `godot-build.cmd` green.
