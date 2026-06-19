# S54 — Render players as the character model with movement-driven walk animation

Severity: feature (visual). Replace the placeholder player mesh with the imported character model
`res://content/characters/ProvaPersonaggioWalkLoop.glb` (a rigged humanoid; **animations: `0_T-Pose` and
`catwalk-loop-378982`**), and drive the walk loop from movement state. Godot-client only — no
server/protocol/Core change. **Verification is largely visual** (human on relaunch); the implementer makes
robust, *tunable* choices and we iterate live.

## What
1. **Instance the model for PLAYER entities** (`EntityKind.Player`) instead of the current capsule/placeholder
   mesh; resource nodes keep their box. Load the GLB once as a `PackedScene` and instance per player; free
   the instance on despawn (mirror the existing capsule lifecycle). Keep the existing name `Label3D` above it.
2. **Drive the position from the existing tween/interpolator** — the model's root sits where the capsule
   did (tile-center, tweened between confirmed tiles). Do NOT change movement/interp logic; just swap the
   visual.
3. **Animation from movement state:** grab the instance's `AnimationPlayer`; when the entity is **moving**
   (its `TileInterpolator` is actively tweening / it stepped this frame) play the **walk loop**, looped;
   when **idle**, stop/pause it (or play the T-pose). Discover the walk clip **robustly** — pick the
   animation whose name is NOT the T-pose (e.g. contains "catwalk"/"loop"), don't hard-fail if Godot
   sanitised the name. Set its loop mode to loop.
4. **Orientation:** rotate the model to face the entity's `Facing` (8-way). Expose a **forward-offset
   const** so we can correct if the model faces the wrong way (glTF/Godot -Z forward; Tripo models vary).
5. **Scale:** Tripo/Character-Creator models often import at real-world metres — almost certainly the wrong
   size for a 1-tile grid. Put the **scale in a single named const** (best first guess ~1 tile tall) so the
   human can tune it in one place on relaunch.

## Files
- `src/Mmo.Client.Godot/MmoClientRoot.cs` (+ a small helper if cleaner) — instance/free the model per
  player; AnimationPlayer wiring; facing rotation; scale/forward consts.

## Notes / risks
- A rigged model per player is heavier than a capsule. Fine for the current handful of clients; note for
  later (LOD/cull when player counts climb — relates to S36b). Don't optimise now.
- Applies to BOTH local and remote players (you want to see your own character).
- If the model imports facing-down/sideways or huge/tiny, that's expected first-try — the consts exist so
  the human fixes it in seconds; surface the exact const names in the briefing.

## Acceptance
- `godot-build.cmd` green (compiles). On relaunch: players render as the character model (not a capsule);
  it **walks (looped) while moving and idles when stopped**, faces the movement direction, at a sensible
  scale; name labels still show; despawn frees it. Scale/orientation/anim-name are tunable consts called
  out for the human. Do NOT commit — Orchestrator reviews. (The Orchestrator runs `godot-build` + an MCP
  spawn/move check; the human signs off the look.)
