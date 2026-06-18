# N21 — Tune the client MoveStep send rate (reduce move traffic from the cadence fix)

Severity: nice-to-have (scalability). Follow-up to commit 5a609be.

## Context

The freeze fix changed `SendHeldMovement` to send `MoveStep` at the **server tick rate** (~20/s while
a direction is held) instead of the step cadence (~7/s), so the server always has a fresh move at its
cooldown boundary and paces steps evenly (fixing the cadence beat). Correct, but it ~3x'd inbound
move traffic per moving player — fine at current scale, worth tuning before many concurrent players.

## Task

Reduce the send rate while preserving the even server-paced stepping. Options to evaluate:
- Send at ~2x the step cadence (e.g. every `EffectiveStepCadenceMs/2`) instead of every tick — enough
  to land a move in each cooldown window without full tick-rate spam.
- Or only send when near the next-step boundary (predictive throttle), still guaranteeing a pending
  move at the server's cooldown.
- Make the rate a named constant/config so it's tunable.

Verify the freeze does NOT return (re-check server `server-steps.csv` cadence clusters ~step interval,
and the human smoothness re-check) and that stress `move/s` drops meaningfully.

## Acceptance

- Lower move/s in a 120-client/60s stress vs current, with step intervals still even (no reintroduced
  freeze) and human movement still smooth.
- `run-checks.cmd` green.

Note: also worth aligning the server `StepCooldownTicks` (floor) vs client `EffectiveStepCadenceMs`
(ceil) definitions while here — a small mismatch (e.g. 100ms vs 150ms) showed in the cadence data and
could let the queue grow; confirm they intend the same step interval.
