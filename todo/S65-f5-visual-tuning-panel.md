# S65 — Split visual tuning to its own F5 panel

Severity: dev-tooling / UX. Today the F4 panel (S60) mixes **server-authoritative** knobs
(`move.stepCooldownMs`, `move.turnDelayMs`, `aoi.interestRadius` — sent via `AdminSetTuning`) with
**client-local visual** knobs (camera zoom, `RockModelScale`, label sizes — applied instantly). Split them:
**F4 = server knobs only; new F5 = visual/rendering knobs (client-local, instant).** Client-only; no server
change. Do S64 first if both are in one branch (same file), or rebase cleanly.

## What
1. **New F5 panel** (toggle like F4) holding the **client-local visual** group, moved out of F4:
   - **Model scales: rock, tree, plant** — per-archetype model scale. `RockModelScale` exists; **add tree
     and plant scale**. Expose all three in `VisualTuning` and have `ModelVisual` read them (tree =
     alberello model; plant = the current placeholder visual) so they're live-tunable like rock is today.
   - Camera zoom min/max, label pixel-size, label height (the existing client-local rows).
2. **F4 keeps only the server group** (`move.stepCooldownMs`, `move.turnDelayMs`, `aoi.interestRadius`).
   Remove the client-local rows from F4 (they live on F5 now).
3. Keep the same apply semantics: F5 changes apply **instantly client-side** (no server round-trip). Keep
   `[Export] RockModelScale` mirrored into the tuning for inspector parity; mirror tree/plant the same way.
4. Match F4's gating for now (admin-only dev tool) — these are dev knobs; note it in the review so the
   human can decide later whether visual-only F5 should be ungated.

## Constraints
- Client-only; no server/protocol change. F5 reuses the F4 panel's row/LineEdit pattern (don't reinvent UI).
- Don't change any default values (rock scale default stays 4; pick sensible tree/plant defaults from their
  current consts in `ModelVisual` so the look is unchanged until tuned).
- Run `.\.shared\skills\mmo-dev\scripts\run-checks.cmd` before/after (likely no Core change; mostly Godot +
  `VisualTuning`). You can't run Godot — Orchestrator runs `godot-build`; the human checks F5 opens with the
  visual knobs, they apply live, and F4 no longer shows them.
- If `GodotClientProjectTests` assert on the F4 panel contents, update them for the F4/F5 split.
- **Safe Local Execution** binds you.

## Acceptance
- `run-checks` + `godot-build` green. F5 opens a visual-tuning panel with rock/tree/plant scale + camera
  zoom + label knobs, all applying live; F4 holds only the server knobs. Review-request →
  `review/review-request-s65-f5-visual-panel.md`. Do NOT commit; do NOT delete the task file.
