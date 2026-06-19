# S55 — Player AnimationTree state machine (idle↔walk) + better name labels

Severity: should-fix (visual polish). Two play-test issues on the S54 character model:
1. **Idle is broken/janky** — S54 drives the raw `AnimationPlayer` with `Play(walk)` / `Stop()`. `Stop()`
   freezes the rig mid-stride instead of a neutral pose, and there's no blending. The character looks
   stuck mid-step when standing still.
2. **Name labels are rough** — overlapping the body, pixelated, no contrast (see play-test image).

Godot-client only (`MmoClientRoot.cs`), no server/protocol/Core change. **Sequencing: do AFTER S53
(prediction) commits — it edits the same file; don't run concurrently.** Verification is visual (human).

## 1. AnimationTree state machine (idle↔walk)
Replace the manual `Play/Stop` with a proper `AnimationTree` + `AnimationNodeStateMachine` per player
instance, reading from the instanced `AnimationPlayer`:
- **States:** `Idle` (plays `0_T-Pose` — placeholder idle; the human OK'd T-pose for now, a real idle
  clip is future content) and `Walk` (the `catwalk-loop-378982` loop). Resolve clip names robustly as
  S54 already does (non-T-pose = walk).
- **Transitions:** Idle↔Walk with a **cross-fade** (~0.12–0.15 s) so it blends instead of snapping.
- **Driver:** keep S54's moving/idle signal (render-position delta + `PlayerWalkHoldSeconds` hold), but
  **verify it reliably flips to Idle when stopped** (tune `PlayerMovingEpsilonSquared` if float jitter
  keeps it "moving"; with no prediction the stopped render position is stable, so idle should latch). Set
  the state-machine travel to `Walk` when moving, `Idle` when stopped.
- Build it in code (AnimationTree node + state machine), or a small player `.tscn` if cleaner — keep the
  existing in-code instancing working. Guard for a missing AnimationPlayer (log once, no crash).
- Note: once prediction (S53) lands, the moving signal could instead come from the predictor's
  is-moving state — fine to use whichever is cleaner, but don't depend on S53 internals if it complicates.

## 2. Name labels (`Label3D` in `_entityLabels`)
- **Position above the head:** the model is ~1.74 tiles tall (native 1.086 × scale 1.6); put the label at
  ~Y 1.9–2.0 (a named const, derived from the model scale) so it sits above, not over the torso.
- **Outline:** dark outline (`OutlineSize` + `OutlineModulate` near-black) for contrast on any background.
- **Render on top:** `NoDepthTest = true` (and/or render priority) so the name never z-fights with / is
  occluded by the model.
- **Crispness:** bump font/`PixelSize` (and consider `FixedSize = true` for constant on-screen size so
  it's readable at distance). Keep it billboarded.

## Files
- `src/Mmo.Client.Godot/MmoClientRoot.cs` (+ optional small player scene/resource).

## Acceptance
- `godot-build.cmd` green. On relaunch: character **blends from walk to a clean standing (T-pose) idle**
  when you stop (no frozen mid-stride), and back to walk smoothly; name labels sit **above the head**,
  have an outline, render on top, and are crisp. Do NOT commit — Orchestrator reviews; human signs off the
  look.
