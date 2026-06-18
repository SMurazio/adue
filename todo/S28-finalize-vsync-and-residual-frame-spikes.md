# S28 — Finalize client VSync/frame-pacing for the shipped client + chase residual frame spikes

Severity: nice-to-have (polish / ship-prep). The user-perceived movement stutter is resolved; this
is the last refinement.

## Context (what we learned)

The residual stutter after S26/S27 was **VSync frame pacing in windowed dev mode**, not render load:
- Game runs at **~1300 fps** uncapped (≈0.77 ms/frame) — never CPU/GPU-bound. The old
  "process ms 16.8" was pure VSync wait.
- Tested `display/window/vsync/vsync_mode`:
  - `1` Enabled (Godot default): the original stutter (occasional missed vblank → doubled frame).
  - `0` Disabled: **best feel**, smooth — but screen tearing, and a slight occasional stutter remains.
  - `2` Adaptive: **much worse** — the Windows DWM compositor fights adaptive vsync in *windowed*
    mode and pacing degrades.
- Current dev setting committed: `vsync_mode=0` (off). Tearing here is a *windowed-mode* artifact.

## Tasks

1. **Finalize VSync for the shipped (fullscreen) client.** The product is a downloadable executable;
   in exclusive/real fullscreen, standard VSync (`1`) behaves correctly (smooth, no tearing, no DWM
   interference). When the real window/fullscreen flow is built, choose and test the vsync mode there
   (likely Enabled in fullscreen; also try Mailbox `3`), and consider a user-facing VSync/FPS-cap
   option. Don't tune this against windowed dev behavior.
2. **Characterize the residual stutter — LEADING THEORY: tile interpolation, not frame pacing.**
   Human observation: the stutter did **not** happen with the old *non-tiled* (continuous) movement;
   it appeared with tile-stepping. With vsync off the client frames are even (~1300 fps), so a
   residual "slight stutter every so often" most likely comes from the **interpolation layer**: the
   client tweens between discrete confirmed tiles (~140-150 ms apart), and if a confirmed-tile update
   arrives late (delivery jitter), the tween starves at a step boundary → brief pause → micro-stutter.
   Earlier `tile_confirmed` traces already showed uneven arrivals (134-300 ms) and swinging
   `queueDepth`.
   - **Confirm:** watch the `MOVE` line `q=` (interpolation queueDepth) / `cadence` during a stutter
     (and/or the `tile_confirmed` trace). `q` dipping to 0-1 at the stutter → starvation; healthy `q`
     but still hitching → tween easing/cadence.
   - **Fix lever:** `TileInterpolator` playout buffer / cadence in `Mmo.Client.Core` — hold a slightly
     larger cushion of confirmed tiles to absorb arrival jitter, and/or adapt tween speed to the
     actual arrival rate. (S13/S14 tuned this for the web client; the Godot client uses the same core
     and may need its own tuning.) Also check whether server snapshot *delivery* itself is bursty.
   - Secondary: if the trace shows even arrivals + healthy queue + even frames yet still a rare blip,
     fall back to the frame-spike angle (F3 HUD graph: GC/engine/OS), which may be the practical floor.

## Acceptance

- A deliberate, documented VSync choice for the shipped client (not just the windowed dev default).
- The residual occasional stutter is characterized (cause identified from the HUD), and fixed if
  cheap or documented as acceptable.
- `run-checks.cmd` + `godot-build.cmd` green.
