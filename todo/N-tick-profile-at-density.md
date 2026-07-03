# N — density tick-profile: MEASURED (2026-07-03) + the first lever PULLED (`afd2da9`). Rest documented, deferred.

**LEVER PULLED (user go, 2026-07-03):** AOI grid cell = radius/4 (was = radius), `afd2da9`. Measured at 200
clients: aoi 5.22 → 3.58 ms (−31%, matching the model), tick avg 10.60 → 7.14 ms, tick MAX 94 → 13.9 ms (the
login-storm spike gone), drift max 58 → 2 ms. Post-fix: 200 all-moving clients ≈ 14% of the 50 ms tick budget,
growth 120→200 now 2.7× (was 3.8×). Also closes the docs/tile-audit.md "spatial-grid cell size" DECISION item.
The remaining AOI superlinearity (0.83 → 3.58, 4.3×) is density×viewers — inherent to streaming AOI; the next
levers (below) stay documented-not-built per the user's directive.

## The profile (2026-07-03, review-stress @ radius 18, Debug, 20 Hz, 60s runs, mid-run /metrics)

| metric (steady state) | 120 clients | 200 clients | growth for 1.67× clients |
|---|---|---|---|
| tickMs avg / max(60s) | 2.80 / 19.4 | 10.60 / 94.1 | 3.8× / 4.8× |
| driftMs avg (steady 5s window max) | 0.03 (0.17) | 0.33 (0.36) | clean both |
| budget **aoi** avg | **0.80** | **5.22** | **6.5× ← THE superlinear pass** |
| budget serialize avg | 0.82 | 2.19 | 2.7× |
| budget network avg | 0.29 | 0.79 | 2.7× |
| budget move avg | 0.03 | 0.06 | 2× (negligible) |
| snap/s | 2087 | 3411 | 1.63× (linear) |
| visible avg/max | 44.3 / 64 | 51.0 / 75 | +15% |
| errors / auth | 0, 120/120 | 0, 200/200 | — |

**Read:**
- **AOI gather dominates and is the only badly-superlinear pass** (6.5× for 1.67× clients — worse than
  clients×entities). Serialize/net grow ≈ clients×visible (mildly superlinear via density). Movement is nothing.
- **At radius 18, 200 clients is comfortably healthy**: steady tick ~10.6 ms of the 50 ms budget (~21% of a
  core), drift ~0 in steady state, 0 errors, ping ≤10 ms. The 94 ms tick / 58 ms drift maxima live in the
  200-connection login storm (ramp), not steady state (steady 5s window: max 17.3 / 0.36).
- The old alarm (107% of a core @200) was at radius 30 — `0d980dc` (30→18) retired the immediate pressure,
  matching the πr² prediction. The remote-shimmer render fix (`daa71fd`) covers the perception side.

**Prime suspect inside the AOI number:** `ResolveEntityGridCellSize = ceil(interestRadius)` → the spatial-grid
cell is as big as the radius itself, so a neighborhood query sweeps a 3×3-cell area ≈ (3r)² — ~7× the actual
interest disc, and every candidate in it pays the distance test. This is exactly the docs/tile-audit.md
"spatial-grid cell size" DECISION item. A smaller cell (e.g. r/2 or a fixed 8) shrinks the over-gather; cheap,
contained, measurable with this same profile. RECOMMENDED first lever when density work resumes.

## Next levers, RANKED for THIS game's topology (documented per the user; build nothing until density demands)

**User insight (2026-07-03) that re-ranks everything:** the game is TOP-DOWN with the camera ≈ the AOI disc (the
zoom clamp ties them, `0dbc5c7`) — there is NO "far away but on screen" tier, so DISTANCE-BASED send-rate LOD
(the Planetside lever) optimizes a tier that doesn't exist here. Crossed off. What applies instead:

- **A. Uniform snapshot rate ≠ sim rate (20Hz sim → 10Hz send). FIRST lever when wanted.** Halves aoi+ser+net
  AND per-client bandwidth (~108 → ~54 kbps) in one global knob, no tiers. De-risked BY the shipped correction
  smoothing + dead-reckoning (`daa71fd` — the harness's bursty scenario proves 100ms sample gaps render clean);
  extrapolate-to-now means zero added render latency, just larger (smoothed) corrections. Small change: decouple
  the snapshot cadence from the tick in the broadcast pass; keep reliable events (spawn/despawn/actions) per-tick.
- **B. Subscription-based incremental AOI (temporal coherence). The deep fix.** Today every tick re-gathers +
  re-tests ~90 candidates × N viewers though the in-AOI set barely changes. Viewers subscribe to grid cells;
  entity cell-crossings update only affected viewers' sets → O(changes) not O(viewers × candidates). Kills the
  dominant remaining term (aoi 3.58ms @200). The classic MMO interest-management endgame; a real design task.
- **C. Shared per-tick entity encoding.** Serialize re-encodes the SAME global entity state per viewer. Encode
  once per tick, memcpy slices into per-viewer packets — in crowds (200 viewers × the same ~50 entities) that is
  most of the 1.48ms serialize term. Contained; pairs well with A.
- **D. One Release-build profile run** (`review-stress -Release`): all numbers here are Debug; true capacity is
  likely 1.5–3× better. Run before any future capacity worry.
- **WoW-class event-driven movement** (start/heading-change/stop + client sim): the far end of the spectrum —
  ~zero per-mover cost, but remote latency up close conflicts with the no-added-lag principle. Only if targets
  go far past ~500.
- **Threading/split (Albion zone-per-process)**: the guardrail says single process until metrics justify — at
  ~14% of a core @200 post-fix, they do not. Albion context: one C# process per zone, hot zones migrated to
  dedicated hardware, zone caps + queue, ~10Hz-class updates with 100-200ms remote interp delay.

Measurement machinery (for the record): `TickBudgetRecorder` per-phase budgets + schedule drift are ALWAYS-ON and
printed by `/metrics` (`budgetMs move/aoi/ser/net/persist/other`, `driftMs`); `review-stress.cmd -Clients N`
captures them mid-run headlessly via its MetricsClient (no human chat-reading needed — the gap noted on
2026-07-02 was already solved by that script).

Design target remains 120–150 visible (holds with big headroom); 200 all-moving bots in one small map is a worst
case. Relates to [[N-remote-smoothness-tooling]].
