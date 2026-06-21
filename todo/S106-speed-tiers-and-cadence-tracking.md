# S106 — Discrete speed tuning: live per-player speed + verified cadence/glide tracking

Severity: S (movement feature — user wants varied speed: mounts/buffs as discrete tiers). Server + client.
Live F6 control. No new netcode — uses the EXISTING per-entity speed path.

## Why

We're staying on grid movement, so speed is tick-quantised: achievable speeds are `walk × (3/N)` for N = effective
cooldown in ticks (walk = 140ms → 3 ticks = 150ms baseline at 20 Hz). Usable tiers: **3.0× (50ms) / 1.5× (100ms)
/ 1.0× walk / 0.75× / 0.6× / 0.5× / …** — fine at the slow end, coarse at the fast end (only 1.5× and 3× above
walk). The per-entity machinery already exists (`SpeedMultiplier` → `EffectiveStepCooldownTicks` →
`MovementSpeedChanged`, the `/speed` command). This task makes speed **tunable live and verifies it feels right
end-to-end** — the user's specific concern was that the render glide must track speed (a slow player must glide
slowly, not zip-then-wait).

## What to build

1. **Live F6 "Move speed (×)" field** for the LOCAL player (admin-gated, mirrors the other F6 fields): on
   Apply/Enter it sets the local entity's `SpeedMultiplier` via the existing server speed path (the `/speed`
   self-target flow — reuse it; do NOT add a new protocol message if the speed path already replicates via
   `MovementSpeedChanged`). Seed from the current multiplier. Clamp to the server's effective range.
   - Document next to the field (or in a comment) the achievable discrete tiers (the `3/N` table) so it's clear
     why e.g. 2.0× snaps to 1.5× — the value quantises to whole ticks server-side.

2. **Verify + fix cadence/glide tracking end-to-end** (the load-bearing part):
   - On `MovementSpeedChanged`, the client must update the predictor AND the cosmetic driver cadence
     (`SetCadence`) so the render glide duration tracks the new effective cooldown. Trace the path
     `MovementSpeedChanged` → `EntityState`/`MmoClient` → `LocalPlayerCosmetic.SetCadence` /
     `LocalPlayerPredictor.SetCadence` and confirm the LOCAL player's cosmetic driver actually receives it (not
     just the interpolator).
   - Confirm a **mid-movement speed change** retargets the in-flight tween's duration (not just the next tween),
     so a slow-down doesn't leave the avatar gliding at the old fast rate to the next tile then waiting.
   - Confirm the commit-step `LeadProgress`/`CommitThreshold` and `ClampLead` remain fraction-of-cadence based
     (they should already scale with `_cadenceMs`), so the commit/lead behaviour is speed-correct.

## Tests
- Client-core: a `LocalPlayerCosmetic` test that a `SetCadence` to a slower value makes the lead glide take
  proportionally longer (render reaches the adjacent tile later), and that an in-flight tween adopts a new
  cadence on a mid-glide `SetCadence`. Server: a test that a `SpeedMultiplier` set yields the expected
  tick-quantised `EffectiveStepCooldownMs` for a couple of tiers (e.g. 1.5× → 100ms, 3× → 50ms).
- Hardened `run-checks` green (`--no-incremental`); Godot build clean.

## Constraints
- No new protocol message if `MovementSpeedChanged` already carries the effective cooldown (it does). Server +
  client. **Safe Local Execution** (scripts only; stop a locking session via `stop-mmo.cmd`, note it). You cannot
  run Godot — Orchestrator does the live check (set 1.5×/3× and a slow tier, confirm the avatar glides at the
  matching speed, no zip-then-wait). If your shell is denied, say so explicitly; don't claim green you didn't run.
- Do NOT commit/push/delete the task file — leave the tree dirty + `review/review-request-s106-speed-tiers.md`;
  the Orchestrator verifies (hardened gate) and commits.

## Acceptance
- A live F6 "Move speed (×)" field sets the local player's speed; the render glide tracks the effective cadence at
  every tier (slow glides slow, fast glides fast, mid-move changes retarget the in-flight tween). Achievable tiers
  documented. Tests + hardened run-checks green; Godot build clean.

## Note (out of scope, parked)
Latency-aware tuning (scale the cosmetic lead + commit reject-grace by EFFECTIVE RTT = measured + 2×sim, capped)
is the known fix for "poor at 100 ms" and is documented in this todo + the design discussion — NOT built here.
Revisit only after a relaunch confirms 100 ms is still bad on the post-S98 build.
