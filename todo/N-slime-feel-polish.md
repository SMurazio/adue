# N — slime hop feel polish (range / delay / height) — AFTER the tile cleanup

User feel-test of Phase C (the real ballistic hop): "jumps really nicely" but the slime "isn't working too well — it
needs a RANGE, a DELAY (between each jump), and a HEIGHT (max height during the jump)." The user explicitly deferred
this until after the tile-reference cleanup (`docs/tile-audit.md`).

## Where it stands (the knobs already exist after the data-driven menu + range conversion)
- **Range** = `hopDistance` (now a continuous world-unit knob in the F1 Monster tab; default bumped 1.0 → 1.5).
- **Height** = `hopHeight` (apex world-units; default 0.5).
- **Delay between jumps** = the move cadence (`moveSpeed`) MINUS the airborne span (`hopAirborneMs`, default 300 ms):
  the slime is airborne for `hopAirborneMs` then RESTS on the ground for (cadence − airborne) before the next hop.

## What "polish" likely means
1. Tune the DEFAULTS so the slime feels good out of the box (range/height/rest) — a live feel pass with the F1 tab.
2. Confirm the "delay between jumps" is intuitive to dial. Right now it's implicit (cadence − airborne). Consider a
   dedicated "hop rest (ms)" or "jumps per second" knob if the two-knob model (moveSpeed + hopAirborneMs) is confusing.
3. Verify the hop arc reads right at the tuned values (apex height, forward reach, the rest beat) on two clients.

## Acceptance
The user dials the slime live in the F1 Monster tab to a hop that feels right (clear range, visible rest between hops,
satisfying height), with the controls intuitive. No code change may be needed beyond default tuning + (optional) a
clearer "delay" knob.
