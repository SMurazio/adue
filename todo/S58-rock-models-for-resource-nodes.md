# S58 — Render Rock resource nodes as the imported rock models (not the box)

Severity: feature (visual). Replace the placeholder box for **Rock** gatherable nodes with the 3 imported
rock GLBs under `res://content/resources/`, varied per node. Godot-client only — no server/protocol/Core
change. Verification is visual (human); make scale/offset **tunable consts** and we iterate.

## The 3 models (static, no animations, ORIGIN-CENTERED → need a Y-offset to ground them)
Measured native bounds (units; grid = 1 unit/tile):
- `M_Rock_Moss_Overgrowth.glb` — H 0.64, W 0.32 × D 0.98, **Ymin −0.32** (flattish mossy rock)
- `M_Rock_Floating_Monolith.glb` — H 0.98, W 0.75 × D 0.68, **Ymin −0.49** (monolith; "floating" — a slight hover is fine)
- `M_Rock_Engraved_Monolith_L.glb` — H 1.91, W 1.75 × D 1.11, **Ymin −0.96** ("L" = large; OK to be the biggest)

## What
1. **Rock nodes only:** for resource entities that are rocks (currently the box via `_resourceMesh`;
   distinguish by `DisplayName == "Rock"` or the resource kind — confirm how Rock vs Tree/Plant is known
   client-side), instance one of the 3 rock GLBs (loaded as `PackedScene`, cached) instead of the box.
   **Tree/Plant keep their box** (no models yet).
2. **Variety, deterministic:** pick the variant by `NetworkId % 3` so it's stable and identical across
   clients (no per-client randomness).
3. **Scale + ground each (per-model tunable consts):** the natives vary a lot, so normalize each to a
   sensible size (~0.8–1.0 tile for moss/floating, the L monolith may stay a bit bigger) and **Y-offset by
   `-Ymin × scale`** so the base sits on the ground (origin-centered → otherwise half-sunk). Starting
   guesses: moss scale ~1.25 (+Yoff ~0.4), floating ~0.85 (+Yoff ~0.42, or a touch less to hover), engraved-L
   ~0.7 (+Yoff ~0.67). One named const per model; human tunes on relaunch.
4. **Depleted state:** preserve the current behavior (the box hides when depleted — `Visible = !Depleted`);
   apply the same hide (or grey) to the rock model. Free the instance on despawn like the box/character.
5. Reuse the S54 GLB-instancing approach (load once, instance per node, free on despawn). No AnimationPlayer
   needed (static). Keep the S57 name label attaching above the node.

## Files
- `src/Mmo.Client.Godot/MmoClientRoot.cs` (+ a small helper if cleaner) — Rock node → rock model instance;
  variant selection; per-model scale/Yoffset consts; depleted hide; despawn free.

## Acceptance
- `godot-build.cmd` green. On relaunch: Rock nodes render as varied rock models (3 kinds) sitting **on**
  the ground at a sensible size, name label above; Tree/Plant unchanged; depleted rocks hide/grey and
  respawn correctly. Scale/offset/variant are tunable consts called out for the human. Do NOT commit —
  Orchestrator reviews; human signs off the look.
