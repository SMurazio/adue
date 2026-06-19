# Client Architecture Design — toward a solid, extensible structure

Decision record + plan. Prompted by `MmoClientRoot.cs` becoming a ~1700-line god-object (entity rendering,
input, camera, HUD, labels, animation, prediction wiring) just as we start adding real content (character
model, rock variants, tree, portal, 2D sprites). The goal: a structure where **adding an entity type, a
model, or a sprite is a small isolated addition — not edits threaded through a monolith.**

## Problem (today)
- One file does everything; entity rendering is `if (kind == ...)` / `DisplayName == "Rock"` branches with
  per-type node-building inline and asset config (paths/scales/offsets) as scattered consts.
- Entity→visual dispatch keys off the gameplay **DisplayName string** (the S58 fragility) — reskinning or
  renaming breaks rendering.
- Not idiomatic Godot (everything built imperatively in one script instead of composed from scenes/classes),
  and untestable.

## Goals (future-proof — the bar the human set)
1. **New entity type / model / sprite = a small, isolated change** (one focused class + one data entry).
2. **Clean separation of concerns**: entity rendering, input, camera, HUD as independent components.
3. **Robust entity→visual dispatch** — a stable identifier from the server, not a gameplay name string.
4. **Data-driven asset config** — model/scale/offset/animation/sprite per archetype lives in one catalog.
5. **An evolution path to Godot `.tscn` scenes** (editor-authored visuals) with no rewrite.
6. Behavior-preserving: the game looks/plays identically after each step — this is structure, not features.

## Target architecture

### 1. Entity-visual class hierarchy (the core)
```
abstract EntityVisual : Node3D           // owns: interpolated wrapper position, name label,
    NetworkId                            //       spawn/update/despawn lifecycle
    Initialize(EntityRenderState, VisualDefinition)
    UpdateFrom(EntityRenderState, now)   // base: position + label; subclasses add their state
    (virtual) OnDespawn()
```
Subclasses, each focused (~30–80 lines), override only their slice:
- **`PlayerVisual`** — character GLB + `AnimationTree` (idle↔walk) + facing (incl. the S59 predicted-turn
  rotation). The S54/S55/S59 player logic moves here, cohesively.
- **`ModelVisual`** — a static / variant GLB (rocks, tree, portal). Carries the deterministic variant + spin
  (rock hash) and the depleted hide for resources.
- **`SpriteVisual`** — a `Sprite3D` billboard for 2D art in the 2.5D world (the magic house).
- **`BoxVisual`** — the placeholder box (fallback / not-yet-modelled types).

The base handles the common 80% (position, label, free); each subclass handles the per-type 20%.

### 2. Visual archetype on the wire (robust dispatch)
Replace `DisplayName == "Rock"` with a stable **`VisualArchetype`** the server sends on `EntitySpawn`
(an enum/`uint16`: `Player, Rock, Tree, Plant, Portal, HouseSprite, …`). The server already knows the kind
(`ResourceNodeRegistry`); this just surfaces a *rendering* identifier decoupled from the display name.
The client maps `archetype → visual class + catalog entry`. Reskins/renames become data, not code branches.

### 3. Data-driven visual catalog
A `VisualCatalog`: `archetype → VisualDefinition { ModelPath/SpriteTexture, Scale, GroundOffset, AnimClips,
VariantPaths, … }`. The per-rock variant/scale/offset consts (and the player/label sizes) move here. Adding
a model/sprite = a catalog entry reusing an existing visual class. Can later become a Godot `Resource`
(`.tres`) tweakable in the inspector — or stay a C# config.

### 4. Visual factory
`EntityVisualFactory.Create(state)` → look up archetype → instantiate the right `EntityVisual` with its
`VisualDefinition`. The renderer never switches on type; the factory + catalog are the only place that knows
the mapping.

### 5. Decompose `MmoClientRoot` into components
The god-object splits into focused pieces (Node children or plain coordinators):
- **`EntityRenderer`** — the visual dictionary + factory; spawn/update/despawn from render states.
- **`CameraController`** — follow + wheel zoom.
- **`InputController`** — WASD, mouse-hold-to-move, hotkeys (F3/F4/F11), harvest.
- **`HudOverlay`** + sub-panels (`StatusPanel`, `MetricsPanel`, `ChatPanel`, `PerfPanel`, `InventoryPanel`,
  `TuningPanel`) — each its own class.
- **`MmoClientRoot`** shrinks to a **composition root**: build + wire the components, drive `_Process`
  (poll client → update camera/entities/hud).

### 6. Evolution to `.tscn` scenes
Visual classes build their nodes in code now (no editor work required). Because the catalog already
abstracts "what model/scene for this archetype," each visual can *later* be backed by a designed `.tscn`
(loaded as a `PackedScene`) without touching call sites — incremental, not a rewrite. This is where
editor-authored art (and an artist workflow) plugs in.

## Staging (behavior-preserving; each stage ships, is reviewed, stays green)
- **Stage 1 — entity-visual hierarchy + factory + `EntityRenderer`.** Lift all entity rendering out of
  `MmoClientRoot` into `EntityVisual` subclasses + an `EntityRenderer`, preserving current behavior (and the
  current DisplayName dispatch *temporarily*). Fold the **new assets in here** as the first proof: a
  `ModelVisual` Tree (alberello) + Portal (portalemagico) and a `SpriteVisual` house (casa_magica). Highest
  value — isolates the growth area immediately.
- **Stage 2 — `VisualArchetype` on `EntitySpawn`.** Add the rendering id, replace the DisplayName dispatch.
  Protocol bump.
- **Stage 3 — `VisualCatalog`.** Move per-archetype asset config (paths/scale/offset/anim/variants) into the
  catalog; visual classes read it.
- **Stage 4 — decompose input/camera/HUD** into their own components; `MmoClientRoot` becomes the thin root.
- **Stage 5 (later) — `.tscn` scenes** for editor-authored visuals + an art workflow.

## Principles
- Each stage is **behavior-preserving** (the game is visually identical; it's structure) and independently
  green (`run-checks` + `godot-build`, human visual sign-off).
- Test the testable seams headlessly (factory mapping, catalog lookups, Core-side state); the look is the
  human check.
- No new gameplay in the refactor — resist scope creep; the new *assets* ride along only because they
  validate the new structure.
