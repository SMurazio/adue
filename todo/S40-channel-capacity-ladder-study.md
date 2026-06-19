# S40 — Channel capacity ladder study (let the per-channel cap evolve from measurement)

Severity: should-do (measurement / decision-enabling). **Depends on S35** (scattered spawn) being
merged — without it everyone clusters centrally and AOI doesn't filter, so per-client cost is
unrepresentative of a real spread-out world.

## Why

The "120–150 clients per channel" figure was always a *design target / stress case*, never a measured
ceiling (see `docs/feature-roadmap.md` Phase 2 & 7). Evidence from the S34 review stress (120 clients,
**central clustering / no AOI filtering**, Debug): tick **1.42 ms avg of a 50 ms budget**, **gc 0/0/0**,
~20 kbps/client. The server is nowhere near saturated at 120. The binding constraints at scale are
expected to be **per-channel bandwidth** and **client rendering**, not server tick. We should set the
cap from data, not a stale guess.

## What (a measurement study, not a code feature)

Run a capacity ladder and record the numbers so the Orchestrator can set the per-channel cap:

1. **Preconditions:** S35 merged; run with `MMO_SPAWN_DISTRIBUTION=scattered` on a large map (e.g.
   1000²) so AOI actually filters. Run in **Release** (`review-stress.ps1 -Release`) for representative
   perf — Debug understates throughput (see `server-tick-performance.md`).
2. **Ladder:** 120 → 200 → 300 → 400 (extend until something gives), **60 s each**.
3. **Capture per rung:** tick/s, tickMs avg/max, driftMs, **per-tick budget buckets**
   (move/aoi/ser/net/persist/other), **gc 0/1/2**, **visible avg/max** (confirm AOI is filtering),
   **bandwidth out per client (clientBytes avg/max, out kbps)**, snap/s, sendFail/bad/netErr, authRate.
4. **Identify the first binding constraint** at each rung (tick budget? bandwidth/client? AOI bucket
   cost? GC?) and the rung where any pass-criterion (authRate 100%, errors 0, tick within budget,
   bandwidth within the UDP/channel target) first breaks.

## Deliverable
- A short results note in `docs/` (table: clients × the metrics above) and a recommended per-channel
  cap with the **named binding constraint** that sets it. This updates the roadmap's 120–150 target
  with a measured number (or confirms it).
- No silent cap changes elsewhere; the Orchestrator decides the cap from this note.

## Acceptance
- Ladder run (scattered spawn, Release, ≥120/200/300/400 × 60 s) with the metrics above captured.
- A written recommended cap + the constraint that bounds it.
- `run-checks.cmd` green (no production code change expected; if a counter is added to measure a
  constraint, it's a small reviewed addition). Do NOT commit results-as-cap — Orchestrator reviews.

## Notes
- This is the disciplined answer to "is 120/150 still right?" — measure before optimizing.
- If a rung reveals a concrete fix (e.g. delta snapshots to cut bandwidth/client, or grid AOI to cut
  the AOI bucket), that becomes its own todo, not scope creep here.
