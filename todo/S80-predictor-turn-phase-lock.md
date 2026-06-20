# S80 — Fix turn desync: phase-lock the predictor's step/turn timing to the server (diagnose first)

Severity: movement feel (the residual "lag"). **Diagnosis-led, then implement if the root is confirmed.**
Option 1 of the approved movement plan, re-aimed at the REAL symptom. Client-only (prefer); parity-critical.

## The repro (from the human, verified with the S79 overlay)
**Straight-line running is fine** — predicted (green) and server (magenta) markers stay glued together. **But
back-to-back turns desync progressively** ("desyncing more and more then snaps back to server position then
again, repeat"). So this is NOT a steady-state drift (my earlier framing was wrong) — it is the **turn /
direction-change handling**: each turn adds divergence that accumulates until a confirm forces a snap.

**Sharpest repro (use as the deterministic test):** spam left-right (E/W) direction changes back-to-back,
then move right — the predicted marker ends up ~5-6 tiles off the server marker. Pure turning, no sustained
travel, produces a large accumulated gap. A predictor test that rapidly alternates intents off the tick grid
and then steps should reproduce + then must NOT diverge once fixed.

## Correctness criterion (the human's key insight — this is the bar)
The test connection is **0 latency** (LAN; `latencyMs == 0`). At 0 latency there is nothing to predict around,
so a FAITHFUL predictor must be **exactly identical to the server position at all times** — predicted tile ==
confirmed tile, every frame, including through turns. The observed gap is therefore a genuine **parity bug**
(the predictor is not a faithful mirror of `WorldEntity`), NOT an "expected prediction lead." The fix is
correct only when, at 0 latency, the green (predicted) and magenta (confirmed) overlay markers stay on the
same tile through arbitrary back-to-back turns.

## Hypothesis to confirm or refute (diagnose FIRST, in the review-request)
The predictor advances its step/turn schedule on **continuous wall-clock** (`LocalPlayerPredictor`
`_nextStepAt`/`_nextEligibleAt` advance by `_cadenceMs`/`_turnDelayMs` from the scheduled time), while the
server fires turns + steps **only on its 50 ms tick grid** (`WorldEntity.TryStep` gated by `_nextEligibleTick`,
turn = `+turnDelayTicks`, step = `+stepCooldownTicks`). On a STRAIGHT run the two stay in lockstep (same
direction every step). On a TURN, the predictor consumes the turn-delay and picks the next step's direction at
a wall-clock instant that the server resolves at a *different* tick-grid instant — so the step around a turn
can go a different direction / land a different tile on each side → a genuine per-turn misprediction. S77
correctly reconciles each by step-seq, but rapid turns produce a misprediction *every* turn, so the human sees
continuous desync + snaps. Confirm this against the code (trace a back-to-back-turn timeline on both sides);
if the real root is different (e.g. the S77 replay not reproducing the turn/turn-delay correctly, or the
turn-seq accounting), surface THAT instead.

## The fix (if the hypothesis holds): phase-lock the predictor to the server's tick cadence
Make the predictor's step/turn schedule align to the server's tick grid instead of free-running on wall-clock,
using the server timing we already put on the wire (S76: snapshot `ServerTick` + per-entity `StepSequence`).
Concretely (the implementer designs the exact mechanism): estimate the server's tick phase from incoming
snapshots and quantise the predictor's `_nextStepAt`/`_nextEligibleAt` (and the turn-delay consumption) onto
that same grid, so a turn resolves at the same logical tick on both sides and the post-turn step direction
matches. The predictor must remain a deterministic mirror of `WorldEntity.TryStep` — keep the
`TurnPathParity_AgainstRealWorldEntity` test green (extend it to drive a tick-offset / mid-tick turn timeline,
which the current grid-aligned test does NOT exercise — that gap is what hid this).

## Tests
- Extend the parity test with a turn timeline where intents arrive BETWEEN server ticks (not grid-aligned), and
  assert tile + facing + step-seq still match the real `WorldEntity` each tick.
- A predictor test reproducing back-to-back turns and asserting the predicted tile does NOT accumulate
  divergence from the server's (no per-turn drift).
- Keep all S77 reconcile tests green.

## Constraints
- Prefer client-only (the wire already carries what's needed from S76). If a server/protocol change turns out
  to be required, STOP and surface it (Orchestrator decision). Keep the predictor↔server parity test green.
  Server is stopped (dev mode). Run `.\.shared\skills\mmo-dev\scripts\run-checks.cmd` before/after (try it; if
  Bash denied, note + continue — Orchestrator runs the gate + a LIVE overlay re-test of back-to-back turns).
  You can't run Godot. **Safe Local Execution** binds you. Do NOT commit, delete the task file, or push.
- **DIAGNOSE FIRST.** If the turn-timing hypothesis is confirmed and the phase-lock is clear + parity-safe,
  implement it. If the root is different OR a parity-safe phase-lock isn't achievable client-only, STOP and
  surface the diagnosis + options rather than shipping a guess (we've mis-aimed twice — correctness over speed).

## Acceptance
- Diagnosis of the turn desync with code refs; if implemented: `run-checks` green incl. the off-grid turn
  parity test + the no-per-turn-drift test, and back-to-back turns no longer accumulate divergence. Review-
  request → `review/review-request-s80-turn-phase-lock.md`. Do NOT commit or delete the task file.
