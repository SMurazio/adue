# S13 — Fix move-stop-move-stop: glide must match cadence, and the remote buffer needs slack

Severity: should-fix (movement bug — every entity, self and remote, stutters). **User-prioritized.**

Reported: own avatar AND all other players "move stop move stop." Two timing bugs in
`src/Mmo.Client.Web/wwwroot/app.js`, both systemic (so they hit everyone, not just self).

## Bug A — glide duration is shorter than the real (tick-quantized) step cadence

S10 advertises the raw `MMO_STEP_COOLDOWN_MS` (140) and the client glides over it
(`tileStepTweenMs`). But the server's actual cadence is quantized to whole ticks:
`StepCooldownTicks = ceil(140 / 50ms) = 3 ticks = 150 ms`. Server steps every **150 ms**, client
glides over **140 ms** → each tile finishes ~10 ms early and `sampleEntityPosition` freezes the
render position until the next step (`startNextConfirmedStep` returns false), a regular per-step stop
for **all** entities.

**Fix A:** glide over the *effective, tick-quantized* cadence. Advertise
`StepCooldownTicks * tickIntervalMs` (the client already has `TickRate` from `ServerHello`, so it can
compute `ceil(cooldownMs/(1000/tickRate)) * (1000/tickRate)` itself).

## Bug B — the remote interpolation buffer has zero slack

`movementInterpolationDelayMs = tileStepTweenMs` (app.js:57, :442) sizes the remote buffer to exactly
one step. Snapshot interpolation needs the buffer *larger* than the send interval so arrival jitter
(tick quantization + the web bridge's poll/WS hops) is absorbed; with zero slack, any late step
underruns the buffer and adds an extra stop. This is why remotes stutter despite being "interpolated."

**Fix B:** give the **remote** buffer slack — set the remote interpolation delay to roughly
**1.5–2× the cadence** (e.g. cadence + ~one extra step), so the next step is always ready when the
current glide ends, even under jitter. Keep **self** at delay 0 (responsiveness, S4).

## Acceptance

- Held movement renders as one continuous, even glide for both the local player and remote players —
  no per-step stop. Verify by eye in the web client.
- `run-checks.cmd` green.

## Note

Fix A+B remove the *systemic* stutter. A finer residual jitter on the **self** avatar specifically
(zero self-buffer + no prediction + the web-bridge hop) is a separate, deliberate trade-off
(networking-design-plan §2) — fully solved only by local-player prediction in the **Godot** client
(which also drops the bridge jitter). Do not chase that here; remotes and the gross self stutter are
what this task fixes.
