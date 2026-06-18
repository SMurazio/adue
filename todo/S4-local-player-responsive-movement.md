# S4 - Local player feels laggy: drop the interpolation buffer for self

Severity: should-fix. **User-prioritized - do this next.** The human reports their own avatar feels
laggy after N6.

## Problem

N6 added a render-in-the-past interpolation buffer for smoothing:
`movementInterpolationDelayMs = tileStepTweenMs` (200 ms) in
`src/Mmo.Client.Web/wwwroot/app.js` - `startNextConfirmedStep` refuses to begin gliding toward a
confirmed tile until ~200 ms after it was received. That delay is correct for **remote** entities
(it absorbs their network jitter so they don't stutter), but it is wrong for the **local player**:
the user commands their own avatar and then waits ~200 ms before it even starts moving - pure,
perceptible input lag stacked on top of the (already accepted) no-prediction confirmation wait.

## Fix

Make the interpolation delay **per-entity**, and set it to ~0 for the local player while keeping it
for remotes:

- Identify self via the existing self id (N4's `CharacterId`-based self matching / `selfNetworkId`).
- For the **self** entity: start the glide toward a confirmed tile **immediately** on arrival
  (delay ~= 0; a tiny value like one tick / ~50 ms is acceptable if zero causes occasional micro-gaps
  on irregular arrival). Keep the constant-velocity **linear glide** over the step duration - the
  goal is to remove the *delay*, not the smoothness.
- For **remote** entities: keep the existing ~200 ms buffer (it's doing its job - don't regress the
  smoothness fix from N6).

This stays inside the "no client prediction" guardrail: the local avatar still only ever moves to
server-confirmed tiles; it just stops waiting an extra buffer-length to start.

## Acceptance

- Holding a direction: the local player starts moving promptly on confirmation and glides smoothly
  (no per-cell stall, no ~200 ms dead time before it reacts).
- Remote players still glide smoothly (the N6 buffer is unchanged for them).
- `run-checks.cmd` green (update `WebClientAssetTests` if it asserts on a single global delay).

## Note / possible escalation

If, after this, the local player still feels laggy **over a real (non-localhost) connection**, the
remaining latency is inherent to no-prediction (server step cooldown + RTT). That is the measured
trigger the design plan reserved for **local-player-only client-side prediction**
(`docs/networking-design-plan.md` section 2) - but treat that as a separate, deliberate decision,
not part of this task. On localhost this fix alone should feel responsive.
