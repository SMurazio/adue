# S12 — Enlarge the world (and distribute spawns) so AOI culling is meaningful

Severity: should-fix (makes the AOI behavior correct rather than "everyone always visible").
**User-prioritized.** Pairs with S11 (radius covers view) and N9 (despawn hysteresis) — same AOI
pass.

## Why

AOI has nothing to cull while the world (64×64) is the same size as the on-screen view, so every
player is always visible. Making the world larger than the view is the real fix: nearby players stay
visible (S11 keeps anything on-screen in-radius), genuinely-distant players are culled.

## Changes

- **Increase world dimensions.** Raise `MMO_WORLD_WIDTH_TILES` / `MMO_WORLD_HEIGHT_TILES` from 64 to
  a moderate size — start around **128×128** (≈4× the view) and tune. Bigger is fine but see the
  density trade-off below.
- **Distribute spawns within a central region** (this is required, or the bump does nothing —
  everyone still piles onto `(8,8)` and stays mutually visible). The `Zone` already supports a set
  of spawn tiles. Seed a cluster of walkable spawn tiles around the map center (a small hub area, or
  a ring), and spawn players/synthetic clients across them. Players congregate (so they see each
  other) but the surrounding world extends past the screen (so wandering apart culls).

## Density trade-off (the thing to balance)

World size and player count together set density. Too large + fully-random spawns ⇒ players almost
never meet ⇒ you see no one. Too small ⇒ everyone always visible (today). Aim for: world a few× the
view, spawns clustered centrally, so a normal play/stress session has players encountering each
other near the hub while distant ones cull. Tune world size + spawn spread together by feel.

## Stress testing

Synthetic stress clients log in through the same path as real players, so they get distributed
spawns automatically once the server distributes — no stress-tool change needed for the realistic
case. This makes stress numbers *representative*: per-client visible count and bandwidth should drop
vs today, and it finally exercises the AOI cull/despawn paths at scale.

But keep the **clustered worst case** measurable: everyone-on-one-tile is the bandwidth *peak*
(everyone visible — what the current ~16 kbps/client numbers reflect). Don't lose it. Provide a way
to force clustering — e.g. a stress profile/flag, or simply a config with a single central spawn
tile — so we can still measure the ceiling. Net: distributed by default, clustered on demand.

## Implications to handle

- **ZoneInfo size:** blocked tiles are a row-major bitset (W×H bits). 128×128 = 2 KB, 256×256 = 8 KB
  — fine, one-time at login (codec cap is 1,048,576 tiles). Very large worlds would eventually need
  region/streamed map delivery (out of scope here; matches N8's no-streaming fence).
- **Web wall rendering:** the debug client builds one mesh per blocked tile; a large border = many
  meshes. If it stutters, batch walls into a single/instanced mesh. Debug-client perf only.
- **AOI scan stays O(n²):** a bigger world lowers per-client *visible count* and bandwidth, but the
  distance scan still checks all pairs. Fine at 120; the scan cost is what grid AOI (design plan D3)
  addresses later when entity counts grow.

## Acceptance

- With the bigger world + central spawns + S11, a player sees others when near/on-screen and they
  cull only when off-screen; players still reliably encounter each other near the hub.
- A 120-client/60s stress run passes (0 errors); per-client visible count and bandwidth should
  *drop* vs the all-visible case. Note the numbers.
- `run-checks.cmd` green; `ServerOptionsTests` / Zone tests updated for the new defaults + spawn set.
