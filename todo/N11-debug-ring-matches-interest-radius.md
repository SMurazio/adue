# N11 — Web debug visibility ring should reflect the actual interest radius

Severity: nit (cosmetic, debug client only).

## Problem

`wwwroot/app.js` hardcodes `debugVisibilityRadius = 96`, but the server's default interest radius is
now 40 tiles (S11). The on-screen debug ring therefore misrepresents what the server actually culls,
which is misleading when reasoning about AOI/visibility.

## Fix

Make the ring reflect the real interest radius. Cleanest: have the server advertise the interest
radius the way it now advertises step cooldown (S10) — add it to `ServerHello` (or `ZoneInfo`) and
have the client draw the ring from the advertised value. Minimal alternative: update the hardcoded
constant to match the current default (40) and note it can drift.

Prefer advertising it: it keeps the debug overlay honest if the server radius changes, and pairs
naturally with the S10 step-cooldown advertisement.

## Acceptance

- The debug ring radius matches the server's interest radius (no hardcoded mismatch).
- `run-checks.cmd` green (protocol round-trip updated if a field is added).
