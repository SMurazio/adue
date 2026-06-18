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

## Plan

1. **Classify with the N18 HUD (F3).** Reproduce, watch the frame-time graph + counters. Key tell:
   do the hitches **taper off after the first 1-2 minutes** (→ JIT warm-up, largely benign) or
   **persist steadily** (→ VSync/frame pacing)? Also note the `max ~143ms` — confirm whether it is a
   one-time startup spike (zone/scene build, first frames) vs recurring mid-play.
2. **Fix the identified cause:**
   - **VSync/frame pacing:** test `display/window/vsync/vsync_mode` options and/or an
     `Engine.MaxFps` cap; on Compatibility/OpenGL a vblank-miss doubles a frame → visible hitch.
   - **JIT warm-up:** if hitches are early-only and settle, it is acceptable; optionally explore
     ReadyToRun / tiered-PGO settings, or simply document it as a startup characteristic.
   - **Per-entity material/mesh churn (secondary):** `CreateEntityNode` allocates a new `CapsuleMesh`
     + `Material(...)` per entity; reuse shared meshes/materials so entity spawn doesn't compile a
     new shader. (Helps the on-appearance hitches even if not the random ones.)
3. Measure each change against the N18 HUD; keep what lowers `hitches`/`max ms`.

## Acceptance

- N18 HUD: `hitches` count materially lower and no recurring mid-play `max ms` spikes during steady
  movement; report before/after.
- **Human re-check:** 1- and 2-client Godot movement is smooth with no perceptible hitching.
- `run-checks.cmd` + `godot-build.cmd` green.
