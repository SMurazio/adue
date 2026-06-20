# S90 — Model B: stop the per-snapshot Confirm from cancelling the steady-state cosmetic lead

Severity: S **but GATED on the live A/B verdict** — only work this if, after feeling model B (S89) live, the
steady-state walk (holding one direction) feels *soft/laggy* (the render trails the server instead of leading)
or shows a faint ~20Hz shimmer/speed-pulse. If B's steady-state feels good as shipped, CLOSE this as won't-fix.
Client-core only. Found in the S89 review (orchestrator), not yet observed live.

## The finding (code-level, from review of `LocalPlayerCosmetic` + the snapshot path)

Model B's `Confirm(tile, facing, now)` (`src/Mmo.Client.Core/LocalPlayerCosmetic.cs:201`) ALWAYS retargets the
render tween toward the confirmed tile and clears `_leadTarget`, **on every call** — including the calls where
the confirmed tile is UNCHANGED.

The local player's `Confirm` runs ~20 Hz (every snapshot), not once per step:
- While moving, between server steps (~150 ms cadence) the player's tile is unchanged, so it is delta-compressed
  out of the payload and reaches `Confirm` via the **S84 delta'd-out path** (`MmoClient.cs:649-661`,
  `localEntity.ApplySnapshot(localEntity.Tile, ...)` → model-B branch → `Confirm(sameTile)`).
- So ~2 of every 3 snapshots call `Confirm` with the SAME confirmed tile while a forward lead is mid-glide.

Effect (traced, not yet seen live): each such `Confirm` retargets the tween from the current render position
back toward the unchanged confirmed tile; the next `Tick` re-arms the forward glide. There is no backward JUMP
(the re-arm heals it the next frame), but the repeated retarget-to-truth **drags the steady-state lead back** —
instead of leading by ~1 tile, the render eases toward the confirmed tile and trails it by a fraction, and the
tween restart every 50 ms can introduce a faint speed-pulse. The **input ONSET stays snappy** (the first `Tick`
arms the glide immediately) and there is **no latch/rubberband** (B's whole point) — this only affects
sustained-walk feel.

Not covered by tests: `AgreeingConfirm_FlowsSeamlessly` fires only the single STEPPING confirm; there is no test
for the ~20 Hz unchanged-tile confirms between steps.

## Fix direction (implementer chooses + justifies)

Make `Confirm` carry positional information only when the tile actually changed:
- **If `confirmedTile == _confirmedTile` (unchanged):** do NOT retarget the render or clear an active
  `_leadTarget` — the confirm tells us nothing new, so leave the forward glide running. (Optionally still adopt
  facing-at-rest.)
- **If `confirmedTile == _leadTarget` (server stepped to where we were heading — agreed):** retarget toward the
  new confirmed tile (== where we're gliding) and let `Tick` re-arm toward the next adjacent — seamless flow.
- **If `confirmedTile` is anything else (blocked / disagreed):** CUT to the confirmed tile (current behavior).

This restores a true ~1-tile cosmetic lead that holds at the `CosmeticLeadTiles` clamp until the server steps,
while keeping the cut-on-disagreement and the exact at-rest convergence. Keep it purely cosmetic — still no
banked tile, no step-seq, no reproject.

## Tests (the gate)

- **Steady-state walk (the missing case):** drive `SetIntent(moving, E)` + a realistic interleave of `Tick`
  (~60 Hz) and `Confirm` (~20 Hz: two unchanged-tile confirms, then one stepping confirm, repeating). Assert the
  render LEADS the confirmed tile by a roughly steady amount (≈ near the 1-tile clamp) instead of trailing it,
  and that the per-frame forward velocity has no large oscillation (no >X backward delta between consecutive
  frames).
- Keep all five S89 invariants green (logic-never-leads, bounded early glide, disagree-cuts, at-rest-exact,
  walkability-gate) and the rest of the suite.

## Constraints

- Client-core only; no server/protocol/wire change; `Tile`/`LocalTile` stays confirmed-only. Model A untouched.
- `.\.shared\skills\mmo-dev\scripts\run-checks.cmd` green before/after. You cannot run Godot — the Orchestrator
  runs the live A/B re-check (hold one direction in B; render should lead smoothly, not trail/shimmer).
- **Safe Local Execution** binds you. One discrete, revertable commit referencing this filename; delete the file
  in that same commit on success. Review-request → `review/review-request-s90-cosmetic-steady-state.md`.

## Acceptance

- In model B, a sustained one-direction walk renders the avatar LEADING the confirmed tile smoothly (bounded by
  the clamp), with no steady-state trail-back and no ~20 Hz pulse; onset still snappy; disagreement still cuts;
  at rest still exact. New steady-state test (fails before / passes after) + all S89 invariants green.
