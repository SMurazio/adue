# S61 — Client refactor Stage 1: entity-visual class hierarchy + EntityRenderer

Severity: refactor (structure). Stage 1 of `docs/client-architecture-design.md`. Lift ALL entity rendering
out of the `MmoClientRoot.cs` god-object into a small **`EntityVisual` class hierarchy** + an
**`EntityRenderer`** coordinator. **Behavior-preserving** — the game must look/play identically; this is
structure, not features. Godot-client only. Read `docs/client-architecture-design.md` first (the full
target architecture); this is just Stage 1.

## What
1. **`EntityVisual : Node3D` (abstract base)** — owns the common 80%: the wrapper node, interpolated
   position from the render state, the name label (S57 styling), and spawn/update/despawn lifecycle. A
   uniform API: `Initialize(state)`, `UpdateFrom(EntityRenderState state, double now)`, `OnDespawn()`.
2. **Subclasses** (each focused; move the existing per-type logic in, don't rewrite behavior):
   - `PlayerVisual` — the character GLB + `AnimationTree` idle↔walk (S55) + facing incl. the S59
     predicted-turn rotation (reads predictor facing). All the current player-model code moves here.
   - `ModelVisual` — a static / variant GLB: rocks (the variant hash + spin + per-model scale/offset +
     depleted hide from S58) and, NEW, the **Tree (alberello)** + **Portal (portalemagico)** as instances of
     this class with their own config.
   - `SpriteVisual` — a `Sprite3D` billboard for 2D art: the **magic house (casa_magica.png)** as a first
     2.5D sprite. Pick a sensible scale/anchor (tunable consts), human verifies.
   - `BoxVisual` — the placeholder box (Tree/Plant that have no model yet, and the fallback).
3. **`EntityVisualFactory`** — `Create(EntityRenderState) -> EntityVisual`. For Stage 1 keep the CURRENT
   dispatch (Player→PlayerVisual; resource `DisplayName=="Rock"`→ModelVisual rock; "Tree" alberello via
   ModelVisual; Plant→BoxVisual; etc.) — the robust `VisualArchetype` is Stage 2; don't do it here. Centralize
   the (still string-based) mapping in ONE place so Stage 2 is a single swap.
4. **`EntityRenderer`** — owns the `Dictionary<uint, EntityVisual>`; on the per-frame render-state sweep it
   creates (via the factory) / updates (`UpdateFrom`) / frees (`OnDespawn`) visuals. `MmoClientRoot` calls
   `EntityRenderer.Sync(renderStates, now)` instead of its current inline entity loop.
5. **`MmoClientRoot` shrinks**: its entity-rendering methods/fields/consts move into the above; it keeps
   input/camera/HUD for now (Stage 4 splits those). Move the per-archetype asset consts (rock paths/scale/
   offset, player scale, label sizes) to live with their visual class for now (the `VisualCatalog` is Stage 3).

## Build it pool-ready + forward-compatible (from the design's cross-cutting pass)
- **`EntityVisual` lifecycle is `Acquire / Reset / Release`**, not just create/free. `EntityRenderer` keeps
  a small **pool per archetype** and reuses a released visual instead of `QueueFree` (AOI churn is constant;
  re-instancing skinned GLBs thrashes). A `Reset(state)` returns a visual to a clean reusable state.
- **Factory is forward-compatible**: an unknown type / a failed asset load → fall back to `BoxVisual` +
  log once, never crash (already the player/rock posture — keep it).
- **`SpriteVisual`** handles its own depth/alpha/billboard explicitly (the 2.5D house) — we hit label
  z-fighting before (`NoDepthTest`); make the sprite's sorting deliberate, with tunable consts.
- **Presentation-only**: visuals READ the computed `EntityRenderState` (position/facing/depleted) and hold
  NO game logic — interpolation/prediction stay in `Mmo.Client.Core`. Don't move any Core logic into Godot.
- Put the new files under **`src/Mmo.Client.Godot/Visuals/`** (one responsibility per file) per the design's
  folder layout — not back into `MmoClientRoot.cs`.
- Add a **`Reset()` seam** on `EntityRenderer` (release all visuals) for a future zone change — wire it
  even if unused now.

## Constraints
- **Behavior-preserving**: players animate + turn as today, rocks render varied + grounded + hide when
  depleted, labels above heads, mouse-wheel zoom, harvest, click/keyboard movement, the F3/F4 panels —
  ALL unchanged. The only *new* visible things are the Tree/Portal models + the house sprite (the assets
  validating the structure).
- Godot-client only; no server/protocol/Core change (archetype is Stage 2). Keep prediction wiring intact
  (PlayerVisual reads the predictor facing the same way MmoClientRoot does now).
- Import the 3 new assets first (`godot-import`) so they have `.import` sidecars.
- You run under accept-edits but cannot run scripts — Orchestrator runs `godot-build` (+ `run-checks` for any
  Core change, likely none); the human visually verifies parity + the new assets. **Safe Local Execution**
  binds you (no hand-rolled launchers / hidden / bypass / PID-kill).

## Forks: surface, don't guess
If extracting cleanly needs the render loop or interpolator access restructured, describe it. Do NOT change
movement/prediction/AOI behavior or the wire. Keep Stage 1 to entity visuals — don't pull in camera/input/HUD.

## Acceptance
- `godot-build` green; on relaunch the world looks/behaves **identically** (players, rocks, labels, anim,
  movement, harvest, zoom, panels) PLUS Tree/Portal models + the house sprite render sensibly. `MmoClientRoot`
  is materially smaller; entity rendering lives in `EntityVisual` subclasses behind an `EntityRenderer` +
  factory. Do NOT commit — Orchestrator reviews; human signs off the look.
