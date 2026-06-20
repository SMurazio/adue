# S95 — Camera: focus on a tunable blend of confirmed-tile vs cosmetic position, with temporal smoothing

Severity: S (camera feel — user request / experiment). Client-only (Godot), no protocol/server change. Live F5
controls. Depends on nothing in S94 but touches the same F5 panel — sequence AFTER S94.

## Why

The camera hard-tracks the local player's COSMETIC render position every frame
(`MmoClientRoot.UpdateCamera` `:1068-1071`: `focus = localState.Position; _camera.Position = focus + offset`),
so model B's cosmetic lead and its release snap move the camera 1:1 — including the pop on the S91 release snap.
The user wants to experiment with a smoother camera that focuses on an **in-between of the real (confirmed) tile
and the character's cosmetic position**, so the cosmetic lead/snap influences the camera less and never pops.

Conveniently, the local render state already carries BOTH: `EntityRenderState.Position` (the cosmetic render)
and `EntityRenderState.Tile` (the server-confirmed tile). So the camera can blend them with no new plumbing.

## What to build (two live F5 levers + temporal smoothing)

In `src/Mmo.Client.Godot/MmoClientRoot.cs`, `UpdateCamera` (`:1046-1073`):

1. **Focus blend lever** `_cameraFollowBlend` in `[0,1]`:
   - `confirmedPos = new Vector3(localState.Tile.X, 0, localState.Tile.Y)` (server truth, jumps a tile per step)
   - `cosmeticPos  = new Vector3((float)localState.Position.X, 0, (float)localState.Position.Y)` (smooth render)
   - `target = confirmedPos.Lerp(cosmeticPos, blend)`. blend = 1.0 ⇒ today's behavior (follow the character);
     0.0 ⇒ follow the confirmed tile only (no cosmetic influence ⇒ no pop, but the camera trails the avatar by
     the lead). An in-between (e.g. 0.5) halves how much the lead/snap moves the camera.
2. **Temporal smoothing lever** `_cameraSmoothing` (a per-second rate, 0 = off/hard-follow like today):
   - Track a persistent `_cameraFocus` (Vector3). Each frame:
     `_cameraFocus = _cameraFocus.Lerp(target, 1f - Mathf.Exp(-_cameraSmoothing * (float)delta))` (frame-rate
     independent; `_cameraSmoothing == 0` ⇒ no smoothing, hard-set to `target`). `delta` is the `_Process(double
     delta)` frame time — thread it into `UpdateCamera` (the call site is `:281`) or stash it in a field.
   - `_camera.Position = _cameraFocus + new Vector3(24, 28, 24); _camera.LookAt(_cameraFocus, Vector3.Up);`
3. **Teleport guard:** if `target` is more than a few tiles (const, e.g. 4) from `_cameraFocus` — respawn / zone
   change / big knockback — SNAP `_cameraFocus = target` instantly instead of gliding the camera across the map.
   Also seed `_cameraFocus = target` on the first frame the local entity has a render state (no spawn glide from
   the origin).

### F5 controls
Add two live fields (same `AddTuningField` + Apply pattern, admin-gated, seeded on open):
- **"Camera follow blend (0=tile,1=char)"** → `_cameraFollowBlend`, clamp `[0,1]`.
- **"Camera smoothing (/s, 0=off)"** → `_cameraSmoothing`, clamp `[0, 30]`.

### Defaults
Default to the CURRENT feel so nothing changes until tuned: `_cameraFollowBlend = 1.0`, `_cameraSmoothing = 0`
(hard-follow the character, exactly as today). The user dials smoothing up and blend down to taste.

## Tests

Camera logic lives in the Godot project (not covered by `run-checks` unit tests, and Godot can't run headless
here). Therefore:
- Keep `run-checks` green (no client-core/server change expected).
- Build the Godot client project (`src/Mmo.Client.Godot/MmoClientGodot.csproj`) clean (0 warnings/errors) via
  the repo `godot-build.cmd` path — the F5 fields + `UpdateCamera` math must compile.
- If you extract any pure math helper (e.g. a `BlendAndSmooth` function) into a testable seam, add a small unit
  test for the frame-rate-independent smoothing + teleport-snap threshold. Optional but encouraged; do not
  contrive a Godot dependency into a unit test.

## Constraints

- Client-only (Godot). No protocol/server/wire change. No movement-model logic change — this only changes what
  the camera focuses on and how smoothly it moves. Default values reproduce today's camera exactly.
- Live F5 controls only — no restart to change blend/smoothing. **Diagnostics-are-live guardrail.**
- **Safe Local Execution** binds you (scripts only; stop a locking session via `stop-mmo.cmd` and note it).
  You cannot run Godot — the Orchestrator runs the live check (blend 0.5 + smoothing ~10/s; walk + release in
  model B and confirm the camera glides smoothly with no pop).
- Do NOT commit, push, or delete the task file — leave the tree dirty + write
  `review/review-request-s95-camera-blend-smoothing.md`; the Orchestrator verifies and commits.

## Acceptance

- Two live F5 levers: camera focus blend (confirmed-tile ↔ character) and camera follow smoothing, both with no
  restart. Defaults reproduce today's camera. With blend < 1 and smoothing > 0 the camera follows a smoothed
  in-between point and the model-B release snap no longer pops the camera. Godot project builds clean; run-checks
  green.
