# S14 — Reduce remote interpolation buffer (2x is too laggy → overshoot/rubber-band)

Severity: should-fix (remote feel). **User-prioritized.**

## Problem

S13 set the remote interpolation delay to `2x` the effective cadence (≈300 ms at the current speed).
That over-buffers: remote players render ~2 tiles in the past, so when another player stops or
changes direction you keep seeing them move, then they snap onto the corrected path — reads as
rubber-banding/overshoot. (This was an over-correction from the "1.5–2x" guidance; 2x was the wrong
end of the range.)

## Fix

Lower the remote interpolation multiplier toward **~1.3x** the effective cadence (≈ one step + a
small jitter margin), in `src/Mmo.Client.Web/wwwroot/app.js`
(`remoteInterpolationCadenceMultiplier`). Tune by feel: small enough that remotes don't visibly lag
on stops/turns, large enough that they don't re-introduce the per-step pause (underrun). Self stays
at 0.

## Acceptance

- Remote players glide without the laggy overshoot-then-correct, and without a per-step pause.
- `run-checks.cmd` green (update the `WebClientAssetTests` multiplier assertion if it pins 2x).

## Note

This addresses the *remote* rubber-band only. The *local* avatar's snap/lag feel is the
no-prediction + web-bridge ceiling (networking-design-plan §2) — not fixable by buffer tuning; it is
the trigger for local-player prediction in the **Godot** client. Do not chase the local feel further
in the web debug client.
