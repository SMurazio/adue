# S11 — Interest radius should cover the on-screen view (stop despawning visible players)

Severity: should-fix (visible regression in feel). **User-prioritized.** Other players despawn in a
small circle while still on screen; the user wants entities to stay visible until they are actually
out of view.

## Cause

S2 set the default `MMO_INTEREST_RADIUS` to 14 tiles (fixing the nonsensical old float-world default
of 96). But 14 tiles is **smaller than the on-screen view**, so entities are culled (`EntityDespawn`)
while still visible on screen. The cull is tied to an arbitrary distance, not the camera view.

## Goal

Visibility should track "on screen," not a small radius. Since the server shouldn't depend on the
client's exact camera (coupling + cheat surface), size the interest radius so it always **exceeds
the maximum on-screen view**: `radius ≈ (half the view diagonal at max zoom-out, in tiles) + margin`.
Then anything visible is always inside the radius, and entities only despawn once truly off-screen.

## Changes

- Raise the default `MMO_INTEREST_RADIUS` to comfortably cover the max-zoom-out view. Derive the
  number from the web client's camera extent at maximum zoom-out (half the visible diagonal in
  tiles) and add a margin; as a starting point try ~32 tiles and **tune by feel until nothing
  on-screen despawns**. (Validation range already allows it.)
- Pair with **N9 (AOI despawn hysteresis)** so entities near the (now larger) boundary don't flicker
  in/out — do these together; they're the same AOI tuning pass.

## Notes / trade-offs (intentional)

- This softens S2's culling. That's fine: on the current 64×64 world the screen shows a large
  fraction of the map, so "cover the view" is near "see most of the world" — and that's affordable
  now because tile-stepped movement is event-gated (bandwidth no longer scales like the old
  continuous-position firehose). Re-check per-client bandwidth in a 120-client stress run after the
  change; it should rise modestly, not explode.
- The real scale story stays as planned: meaningful AOI culling returns when the **world is larger
  than the view**, handled by a bigger map + grid/spatial-hash AOI (design plan D3). Sizing the
  radius to the view now is the correct interim behavior, not a reversal of that direction.

## Acceptance

- In the web client, players stay visible as long as they're on screen and only despawn once
  off-screen (at default zoom and zoomed out).
- No visible flicker at the boundary (with N9).
- A 120-client/60s stress run still passes (0 errors); note the per-client bandwidth delta.
- `run-checks.cmd` green; update `ServerOptionsTests` for the new default.
