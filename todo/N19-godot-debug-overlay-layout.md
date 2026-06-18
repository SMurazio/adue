# N19 — Clean up the Godot client debug overlay layout

Severity: nice-to-have (UI polish). The debug overlay works but is visually disorganized.

## Problems (from a live screenshot)

- **Overlapping text:** the N18 "PERF HUD (F3)" panel overlaps the S25 movement/FRAME debug text in
  the top-left status block (they're both positioned around y≈138 and the status block grows with the
  movement-debug + frame lines).
- **Duplicated frame readout:** the `FRAME ms/hitches/gc` line appears twice — once via
  `FormatFrameDebug()` appended to the status label (S25) and again in the N18 perf panel.
- **Loose vertical spacing:** the perf rows are spread down the whole left edge with large gaps
  (~2x the font height), and the frame-time graph is wedged between rows.
- **No grouping / readability:** everything is bare text floating over the 3D scene; panels aren't
  aligned to screen corners and have no backing, so they're hard to read over light geometry.

## Scope

Reorganize the overlay in `MmoClientRoot` (overlay built in `BuildOverlay`) into clean, non-
overlapping, corner-anchored panels using proper Godot layout containers
(`PanelContainer` + `MarginContainer` + `VBoxContainer`) instead of hand-tuned absolute offsets:

- **Top-left:** player/connection status + controls help (one block).
- **Perf panel (F3 toggle):** its own bordered/semi-transparent panel — compact rows (fps, frame
  ms last/max, process/physics, draw/objects, primitives, video/static/managed MB, nodes, gc,
  hitches) with the frame-time graph cleanly placed at the bottom of that panel. Consistent line
  spacing, no gaps.
- **Top-right:** server metrics block.
- **Bottom-left:** chat log; **bottom:** chat input.
- **Remove the duplicate frame readout** — keep frame/GC stats in the F3 perf panel only; drop the
  `FormatFrameDebug()` append from the status label (or vice-versa, but only one place).
- Give panels a subtle semi-transparent background for legibility over the scene.

## Constraints

- Keep it allocation-light (preserve S25/N18: reused `StringBuilder`, 10Hz text throttle, graph
  redraw only when visible). The layout cleanup must not reintroduce per-frame allocations or hitches.
- No gameplay/render changes; overlay-only.

## Acceptance

- Overlay panels are corner-anchored, non-overlapping, and readable over the 3D scene; perf panel is
  one tidy block (rows + graph); no duplicated frame info.
- F3 still toggles the perf panel cleanly.
- HUD does not add frame hitches (verify with the perf panel itself).
- `run-checks.cmd` + `godot-build.cmd` green.
