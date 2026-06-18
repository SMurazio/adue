# S10 — Make movement quicker (faster step cadence), server-driven

Severity: should-fix (feel). **User-prioritized.** The human finds movement sluggish and wants a
more modern feel. Decision: address speed first (cheap, reversible); keep server-authoritative-only
(no prediction yet — see networking-design-plan §2).

## Goal

Make walking quicker by lowering the step cadence, and make the **server own that speed** so the
client glide stays in sync automatically (no hardcoded client constant to drift).

## Changes

1. **Lower the default step cooldown.** `MMO_STEP_COOLDOWN_MS` default 200 → **140** (~7 tiles/sec
   vs ~5). This is a feel knob — tune by feel; it's already validated to [50, 5000]. Pick the value
   that feels good in a quick web test.
2. **Server advertises the step cooldown so the client derives its glide from it.** Today the client
   hardcodes `tileStepTweenMs = 200` (and remote `movementInterpolationDelayMs` from it); if the
   server cooldown changes, the client desyncs (glide ≠ cadence → stall or queue buildup). Fix by
   advertising the cooldown:
   - Add `StepCooldownMs` to `ServerHelloMessage` (it already carries `TickRate`, a sibling timing
     value) — small protocol change, bump `ProtocolCodec.Version`. (`ZoneInfo` is an acceptable
     alternative home if you prefer per-zone speed, but ServerHello is simpler/global for now.)
   - `WebBridgeSession` already forwards ServerHello to the browser — include `stepCooldownMs`.
   - Web client sets `tileStepTweenMs` (and the remote interpolation delay) from the advertised
     value instead of the hardcoded 200. Self delay stays 0 (S4).

## Scope fence

- No client-side prediction here (that's a separate, later, Godot-only decision — §2).
- No "run vs walk" speeds yet — single cadence. (Run is a fine later gameplay addition.)

## Acceptance

- Walking is visibly quicker and still a smooth continuous glide (no per-cell stall, no queue
  buildup) on the web client.
- Changing `MMO_STEP_COOLDOWN_MS` server-side is reflected in the client glide with **no client
  edit** (the hardcode coupling is gone).
- `run-checks.cmd` green; protocol round-trip test updated for the new ServerHello field.
