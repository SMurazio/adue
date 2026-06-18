# N9 — AOI despawn churn: entities thrash across the interest boundary

Severity: nit-tier (efficiency; metrics-observed). Not a correctness bug — the protocol handles it.

## Problem

A 120-client/60s stress run sends **~61k `EntityDespawn` vs ~14.6k `EntitySpawn`** (~1000
despawns/sec). Because spawn is sent once per (recipient, entity) pair (`KnowsEntity` stays true
forever) but despawn fires every time an entity drops out of the last-snapshot set, players
random-walking near the radius-14 boundary oscillate in/out of each other's AOI and generate a flood
of reliable `EntityDespawn` messages — reliable-channel traffic + per-tick message/encode allocation
(a contributor to the GC tail in N10). The existing `SnapshotRetentionBonusDistanceSquared` softens
re-selection ordering but does not stop the oscillation.

## Fix (when AOI is next touched — pairs naturally with grid AOI, design plan D3)

Add **AOI hysteresis** so an entity isn't despawned the instant it dips out:
- Separate enter vs exit radius (exit radius > enter radius), or
- Require N consecutive ticks (or a short dwell) out of interest before sending `EntityDespawn`.

Either bounds the churn to genuine departures.

## Acceptance

- A 120-client/60s stress run shows `EntityDespawn` count drop substantially (no ~1000/s thrash)
  with no change to what a client ultimately sees.
- `run-checks.cmd` green.

Note: this is metrics-gated polish; fold it into the grid/spatial-hash AOI work (design plan D3)
rather than doing a one-off if that work is near.
