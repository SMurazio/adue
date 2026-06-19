# S57 — Shrink name labels + raise them above the head (players AND gatherables)

Severity: nit (visual polish). Play-test: the S55 name label is **too big** (bigger than the character)
and sits over the body instead of above the head. Same wanted for resource/gatherable labels.

Client-only (`MmoClientRoot.cs`). **Sequence after S56** (it edits the same file).

## What
1. **Players:** shrink the name label substantially (~1/3 current on-screen size) and raise it clearly
   **above the head**. Current S55 consts: `PlayerLabelPixelSize = 0.0018f`, `PlayerLabelFontSize = 64`,
   `FixedSize = true`, `PlayerLabelHeight = PlayerModelScale * 1.225f` (≈1.96). The text spans too much
   screen at this size so it overlaps the body. Reduce `PlayerLabelPixelSize` (~0.0006–0.0008 as a start)
   and raise `PlayerLabelHeight` a touch so the label floats above the ~1.74-tile-tall model. Keep the
   outline + `NoDepthTest` (render-on-top) + billboard.
2. **Gatherables/resources:** apply the same treatment to the resource node labels (the old `AttachLabel`
   path) — small, outlined, on-top, positioned **above the node box**, not centered on it.
3. Keep both readable; this is a size/position tune. Final values are a human visual check — put them in
   named consts so they're one-line tunable.

## Acceptance
- Player + resource names are small and float above the head/node (not overlapping the body/box), still
  outlined and crisp. `godot-build` green; human signs off the look.
