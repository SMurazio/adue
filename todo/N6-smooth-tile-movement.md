# N6 — Tile movement still looks stepped; make it a continuous smooth glide

Severity: should-fix (web debug client feel). **User-prioritized** — the human specifically asked
for this; do it ahead of other N items.

## Problem

Holding a movement direction renders as "glide, stop, glide, stop" (stepped) instead of one
continuous walk between cells. Two causes in `src/Mmo.Client.Web/wwwroot/app.js`:

1. **Per-segment ease-in-out.** The tween eases each single-tile move:
   `const eased = alpha * alpha * (3 - (2 * alpha));` then `lerpVectors(from, to, eased)`
   (`app.js:1318-1319`). Smoothstep decelerates to ~0 velocity at every cell boundary and
   re-accelerates, so continuous walking pulses once per tile.
2. **Glide stalls between updates.** `tweenDurationMs = tileStepTweenMs = 200`
   (`app.js:54`, `:858`) equals the server step cooldown (200 ms), but the next tile arrives later
   (cooldown is quantized to ticks, then the snapshot is sent + travels). The tween reaches the
   target, `alpha` clamps to 1, and the mesh sits still until the next tile restarts it
   (`updateEntityTileTween`, `app.js:744-778`; sampler at `app.js:1316-1320`). That dwell at each
   cell is a visible pause.

## Fix (approach, not prescriptive line edits)

Move from fixed-duration per-segment tweens to **continuous constant-velocity interpolation toward
confirmed tiles** — the snapshot-interpolation model from the design plan (N1), adapted to tiles:

- **Linear interpolation for movement** (drop the per-segment smoothstep), so chained tiles form one
  constant-velocity glide. If a subtle ease is wanted, apply it only to the start/stop of a *walk*,
  never per tile.
- **Eliminate the inter-step stall.** Keep the mesh moving until the next tile is already in hand.
  Practical options (pick one):
  - Maintain a small per-entity queue/target of confirmed tiles and advance the render position at a
    constant rate (tiles/sec ≈ 1000 / stepCooldownMs), chaining segments with no dwell; or
  - Render with a small interpolation delay (~1 step / ~200 ms buffer) and continuously lerp toward
    the latest confirmed tile, so a slightly-late update doesn't cause a pause.
- Keep the large-delta **snap** for re-entry/teleport (don't glide across the map) — that part is
  correct; only the normal one-tile case should be the continuous glide.
- This still has **no client prediction**: the render position only ever chases server-confirmed
  tiles, just smoothly and without stalling. The local player will feel ~one step behind, which is
  expected and acceptable for this slow movement.

## Notes

- This is the web debug client, but the interpolation approach is client-agnostic — the same model
  should carry to the future Godot client, so the work isn't throwaway.
- Pair the glide rate to the actual step cadence; if `MMO_STEP_COOLDOWN_MS` changes, the client
  should derive its rate from the server's tick rate / step cooldown rather than a hardcoded 200 ms
  (the server already sends tick rate in `ServerHello`).

## Acceptance

- Holding a direction renders as one continuous, constant-velocity glide across multiple cells — no
  per-cell deceleration and no pause between cells.
- Re-entry/teleport still snaps (no cross-map glide).
- `run-checks.cmd` green (WebClientAssetTests still pass; update them if they assert on the tween
  shape).
