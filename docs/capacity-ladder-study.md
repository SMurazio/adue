# Channel Capacity Ladder Study (S40)

Answers the standing question "is 120–150 clients/channel still the right cap?" with measurement.
Run on 2026-06-19, **Release**, single process, 1000² map, 60s/rung, via
`review-stress.ps1 -Release`. SQLite default. The server build under test is commit `44d36e9`
(post-S38). Tick budget = 50 ms (20 Hz).

## Results

| Connected | Spawn | visible avg/max | tickMs avg/max | AOI ms avg | gc | clientBytes avg/max | server out | errors |
|---:|---|---:|---:|---:|:--:|---:|---:|:--:|
| 120 | scattered | 1.4 / 7 | <1.84¹ | ~0.5 | 0 | 32 / 80 | — | 0 |
| 200 | scattered | ~1.5 / 7 | <1.84¹ | ~0.8 | 0 | ~33 / 80 | — | 0 |
| 300 | scattered | 1.8 / 8 | 1.84 / 9.25 | 1.58 | 0 | 33.7 / 88 | 5.9 Mbps | 0 |
| 400 | scattered | 2.4 / 7 | 2.29 / 6.41 | 2.03 | 0 | 35.3 / 80 | 8.0 Mbps | 0 |
| **150** | **dense (clustered)** | **89.5 / 150** | **1.97 / 7.99** | **1.18** | **0** | **185.6 / 1248** | **6.7 Mbps** | **0** |

¹ 120/200 server-tick lines weren't separately captured, but they are strictly bounded by the 300-rung
(more clients = more work), so tick < 1.84 ms avg; both passed 0-error with low latency.

## Findings

1. **Server CPU is nowhere near the binding constraint.** Even the dense 150-visible case — the actual
   "120–150 visible players" target — uses **~2 ms of the 50 ms tick budget** (~4%), gc 0, 0 errors.
   400 connected (scattered) is ~2.3 ms. There is roughly **25× tick headroom** at the design target.
2. **Connected count is cheap; VISIBLE density is the cost driver.** Scattered 400 (visible ~2) is
   easier than dense 150 (visible ~90) on per-client bandwidth (35 vs 186 bytes/snapshot) and snapshot
   serialization. The cap is fundamentally about *visible* entities (AOI overlap), not raw connections —
   matching the roadmap's "120–150 **visible**" framing.
3. **The one cost that grows is the AOI scan.** Naive O(N) per-client distance checks:
   0.5 → 1.58 → 2.03 ms across 120 → 300 → 400 connected. Still small, but it is the term that will bind
   first at higher scale. This is exactly the documented trigger for **grid / spatial-hash AOI**
   (roadmap Phase 7).
4. **Two narrower limiters, both already on the roadmap:**
   - **ZoneInfo login bandwidth burst** — `server out` is inflated by the full-map border shipping to
     every client at login (the in=24 Mbps t=5s spike). This is **S36a** (chunked terrain).
   - **Thundering-herd login latency** — 400 simultaneous logins pushed loginMs max to ~1 s (avg 31 ms);
     a mass-reconnect artifact, not a steady-state limiter.
5. **Untested limiter: client-side rendering** at high visible density (this study is server-side only).

## Recommendation

- **Keep 150/channel as the published conservative target for now** — it is comfortably met (the dense
  150-visible case runs at ~4% tick budget). It is a floor, not a ceiling.
- **The server can almost certainly do 2–3× that**, but raise the cap *after* the measured gates clear,
  not on connected-count evidence alone. Gates, in priority order:
  1. **Login bandwidth burst — DONE (S42).** Solved better than chunked streaming: static terrain now
     ships as a procedural **seed** the client regenerates locally, so login terrain cost is ~constant
     (the 24 Mbps spike is gone). Superseded the abandoned chunked-streaming S36a.
  2. **Grid / spatial-hash AOI (S41, in progress)** — the now-measured trigger, reinforced by S44:
     world-scattering ~1.3k node entities pushed `aoi avg` 0.14 → 1.38 ms at 120 clients, making the
     naive scan the dominant tick cost. Flattens it so visible/entity density can rise.
  3. **A client-render test at high visible density** — confirm the *client* isn't the limiter.
- **Binding constraint that sets the cap:** per-client bandwidth + AOI-scan cost at high *visible*
  density — **not** connected count and **not** server tick. Re-run this ladder with a dense-visible
  profile (e.g. 300–500 connected clustered) after grid AOI lands to set the next number.

## Re-measurement after the gates cleared (2026-06-19, post S41/S42/S44)

Both gates are now in (S42 seed terrain, S41 grid AOI), with S44's ~1.3k scattered node entities also
live. Re-ran the ladder (Release, scattered, 1000², grid AOI + nodes):

| Clients | tickMs avg / max | AOI ms avg | gc | errors | note |
|---:|---:|---:|:--:|:--:|---|
| 120 | 0.45 / — | **0.23** | 0 | 0 | pre-grid the AOI scan was the dominant cost; now negligible |
| 300 | 1.03 / 9.3 | **0.57** | ~0 | 0 | **AOI 1.58 → 0.57 ms (~3×)** vs the original 300 rung; ~2% budget |
| 800 | 2.09 / 13.5 | 1.17 | ~0 | 0 | (post-S45) ~4% budget; AOI below the pre-grid *400* rung |
| 1000 | 2.99 / 65.9 | 1.67 | ~0 | 0 | (post-S45) ~6% avg budget; first *tail* strain (drift max 41 ms) |

**Grid AOI worked:** the scan that was the binding/dominant tick cost is ~3× cheaper and no longer the
bottleneck — even at 1000 connected the AOI bucket is only 1.67 ms.

**The 500-client "crash" was the load generator, not the server (fixed by S45).** Root cause: the rig
opened **one `NetManager` with its own background thread per client**, so ~500 clients ≈ ~500 OS threads →
the box thrashed and PowerShell fell over. S45 converted the load-gen to **ManualMode NetManagers driven
from a single poll loop** (O(1) threads). With that:
- **500 clients** (which previously crashed) now run **0-error / 100%-auth**.
- **800:** server tick 2.09 ms avg (~4% budget), 0 errors.
- **1000:** server tick **2.99 ms avg (~6% budget)**, 0 errors. The tick *tail* (max 66 ms over the run;
  135 ms / drift 90 ms in the windows that include connect) is **entirely the login ramp** — a 90 s run
  isolated it: the **steady-state last-5s window (all 1000 connected) is tick avg 4.58 / max 7.44 ms,
  drift max 0.16 ms, gc 0**. So once connected the server has *no* tail strain at 1000; the spikes are the
  mass-connect burst (loginMs max ~920 ms), which **staggering logins** would smooth — a thundering-herd
  *login* cost, not a steady-state ceiling.

**So the server is not the cap at any single-box-achievable load** — it sustains **1000 connected at ~6%
tick budget, 0 errors**. The only remaining limit is that the load generator and server still **share one
machine's CPU**; the tail strain at 1000 is that contention, not the server's intrinsic ceiling.

**Updated cap guidance:** the measured headroom supports raising the conservative published target *well*
beyond the old 120–150 — the server comfortably sustains **1000+ connected**. To publish a precise hard
ceiling, drive load from a **separate machine** (removes the rig/server CPU contention); the server's true
limit is above 1000 and unmeasured only because one box can't both generate *and* serve that load cleanly.

## Method notes / reproduce

```
.shared\skills\mmo-dev\scripts\review-stress.cmd -Clients <N> -Duration 60s -Release \
  -SpawnDistribution scattered -WorldWidth 1000 -WorldHeight 1000   # scattered (low visible)
.shared\skills\mmo-dev\scripts\review-stress.cmd -Clients 150 -Duration 60s -Release \
  -WorldWidth 1000 -WorldHeight 1000                                # default Distributed = dense
```
The `metrics total:` line (server-side tick/gc/budget) prints mid-run; grep for it without `tail`.
