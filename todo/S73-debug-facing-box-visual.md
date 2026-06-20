# S73 — F5 debug toggle: render players as a box + facing-direction arrow

Severity: dev tooling (diagnostic). A live toggle in the **F5 visual panel** that swaps the player visual
from the character model to a plain **debug box with a small arrow at its base pointing in the entity's
facing direction**. Purpose: make facing + per-step movement legible while debugging movement feel (turns,
direction changes, the rubberband) — the character model makes facing hard to read; a box + arrow makes it
obvious. Client-only; live toggle (per the "diagnostics are live in-client toggles" guardrail) — no relaunch.

## What
1. Add a **`CheckBox` to the F5 visual panel** (beside the uncap-fps / frame-log toggles), e.g. **"Debug
   facing box"**, backed by a `VisualTuning` flag (`DebugFacingBox`), default off. Toggling applies live
   (no Apply) like the other F5 toggles.
2. When **on**, every **Player**-kind entity (local + remote) renders as a **box** (reuse `BoxVisual`'s entity
   box mesh/material) **plus a small arrow/wedge at the base** that points in the entity's **facing**
   (`EntityRenderState.Facing`, the 8-way `Direction8` → a Y-rotation). The arrow updates as facing changes
   (read it in `UpdateFrom` each frame). When **off**, players render normally (`PlayerVisual` model).
3. Toggling re-syncs existing player visuals: have `EntityRenderer` release + re-acquire the player visuals
   (or swap their child nodes) so the change is immediate for already-spawned players, not just new ones.

## Implementation notes
- Cleanest: the `EntityVisualFactory`/`EntityRenderer` reads `VisualTuning.DebugFacingBox`; for Player
   entities it builds the debug box+arrow visual instead of `PlayerVisual` when the flag is set. The F5
   toggle flips the flag and tells `EntityRenderer` to rebuild the player visuals (reuse the pool
   release/re-acquire path).
- The arrow: a small flat triangle/cone/prism mesh at the box base (y≈0), rotated to the facing direction.
   Map `Direction8` → degrees (N/NE/E/… → the same world mapping the avatar uses; verify it points the way
   the avatar actually walks, not 180° off).
- Reuse existing meshes/materials where possible; keep it cheap (it's a debug view).

## Constraints
- Client-only; no protocol/server/movement change. Live toggle (no relaunch); admin-gated like the rest of
   F5 (note it). Default off → zero change to normal rendering.
- Run `.\.shared\skills\mmo-dev\scripts\run-checks.cmd` before/after (try it; if Bash denied, note + continue
   — Orchestrator runs `godot-build`). If `GodotClientProjectTests` assert F5 panel contents, update them.
   You can't run Godot — Orchestrator runs `godot-build`; the human checks the box+arrow + that the arrow
   matches the walk direction. **Safe Local Execution** binds you.

## Acceptance
- `godot-build` green; F5 has a live "Debug facing box" checkbox; on → players become a box with a facing
   arrow that tracks their direction (and matches the way they walk), off → normal model; applies to
   already-spawned players. Review-request → `review/review-request-s73-debug-facing-box.md`. Do NOT commit
   or delete the task file.
