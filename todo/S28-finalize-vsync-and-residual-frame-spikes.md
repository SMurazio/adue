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
2. **Characterize the residual frame spike.** With vsync off (~1300 fps) a slight stutter still
   happens occasionally — a rare single long frame (`gc` counts are tiny, so likely engine/OS/GC
   blip). Use the F3 HUD frame-time graph + hitch counter to catch what coincides with it; fix only
   if it's something cheap (e.g. throttle/avoid an allocation or a periodic engine call). It may be
   the practical floor for a windowed dev client.

## Acceptance

- A deliberate, documented VSync choice for the shipped client (not just the windowed dev default).
- The residual occasional stutter is characterized (cause identified from the HUD), and fixed if
  cheap or documented as acceptable.
- `run-checks.cmd` + `godot-build.cmd` green.
