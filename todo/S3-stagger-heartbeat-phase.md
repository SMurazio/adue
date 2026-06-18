# S3 — Synchronized full-snapshot heartbeats cause periodic tick spikes

Severity: should-fix (scaling/smoothness; introduced as a side effect of the S1 fix)

## Problem

Fixing S1 made the full-snapshot heartbeat fire on schedule again — good. But the heartbeat has **no
per-session phase offset**: `SnapshotHeartbeatTicks()` is a flat interval
(`src/Mmo.Server/Runtime/GameServer.cs:518`) and `ShouldSendFullSnapshot` simply compares
`serverTick - _lastFullSnapshotSentTick >= heartbeatTicks`
(`src/Mmo.Server/Runtime/ClientSession.cs:111`). Clients that authenticate close together get their
first full snapshot on nearly the same tick, then re-sync every `heartbeatTicks` (≈1 s at 20 Hz) on
the **same tick** — a synchronized burst where the server serializes full visible sets for many
clients at once.

## Evidence

120-client / 60s stress, both the implementer's run and an independent review run:
- `tickMs avg/max ≈ 4.6 / 33–42 ms` — average is fine, but a periodic spike hits 66–84% of the
  50 ms budget.
- On the spike tick, the measured budget buckets (`aoi ≈ 7.6 ms`, `ser ≈ 4.8 ms`) account for only
  ~12 ms of the ~33 ms; the remainder is GC pause from per-tick allocation concentrated on the
  burst tick.

Under budget at 120, but the burst scales with the number of concurrently-logged-in clients, so it
becomes a tick-budget overrun (dropped/cascading ticks via the catch-up loop) somewhere above ~150 —
exactly the scaling ceiling we care about.

## Fix

Stagger the per-session heartbeat phase so full snapshots spread evenly across the heartbeat window
instead of aligning. Options (pick the simplest):
- Initialize each session's heartbeat phase by an offset, e.g. seed `_lastFullSnapshotSentTick` at
  authentication so the first full snapshot lands at `spawnTick + (NetworkId % heartbeatTicks)`, or
- Gate `ShouldSendFullSnapshot` on `(serverTick + NetworkId) % heartbeatTicks == 0` style phase
  distribution.

Either way, no two-thirds of the fleet should heartbeat on the same tick.

Complementary (do NOT do here — it's the WorldState/data-oriented work in the design plan): reducing
per-tick allocation on the snapshot path will shrink the GC component of the spike. Note it; don't
scope it into this task.

## Acceptance

- 120-client / 60s stress: `tickMs max` is materially closer to the average (no ~1 s periodic spike
  into the 30–40 ms range); ideally max stays comfortably under ~half the budget.
- S1 heartbeat regression test still passes (full snapshots still arrive on schedule per client).
- `run-checks.cmd` green.
