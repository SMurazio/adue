# S63 — Free turns: a turn costs a small turn-delay, not a full step cooldown

Severity: movement feel (the priority fix from `docs/movement-and-architecture-notes.md` §1.1). Our S59
turn-then-move makes a direction change **consume a full step cooldown (~150 ms)** before the next action —
that heaviness is why movement "isn't quite there." A turn should cost only a small **turn delay** (default
~80 ms, tunable), so whipping the cursor rotates in place quickly and settling on a direction steps at the
normal cadence. Server + client must stay in lockstep (prediction parity). **Behavior change is intentional
and confined to turn timing — tile-stepping, AOI, snapshots unchanged.**

## What
1. **Server — `WorldEntity.TryStep`** (the S59 turn branch). Today a `direction != Facing` turn sets
   `_lastStepTick = serverTick` (full cooldown). Change so the turn instead makes the **next step/turn
   eligible after `turnDelay`** (not the full step cooldown). Keep the **rotate-in-place-on-whip** behavior:
   a direction that lasts only one beat still *turns* (no move); it must NOT become instant (instant/zero
   would let rapid direction changes move the entity). Mechanism is your call (e.g. a separate
   next-eligible-tick, or backdating `_lastStepTick` by `cooldown - turnDelay`); pick the cleanest.
2. **Client — `LocalPlayerPredictor`** (the S59 turn branch). Mirror EXACTLY: a turn advances the predicted
   next-step time by `turnDelay`, not the full cadence — so prediction matches the server tick-for-tick.
3. **Tunable** — add **`move.turnDelayMs`** to `ServerTuning` + `ServerTuningRegistry` (clamp e.g.
   `[0, 1000]`), seeded from a new `ServerOptions` default (80 ms). Wire it into the **F4 tuning panel**
   (server group, like `move.stepCooldownMs`) so the value can be felt live.
4. **Parity plumbing** — the predictor needs the authoritative turn delay. Advertise **`turnDelayMs` in
   `ServerHello`** (alongside the existing step cooldown), bump `ProtocolCodec.Version` (→ v18), and have the
   predictor use it (falling back to the 80 ms default). The F4 panel updates both sides (client predictor +
   server via `AdminSetTuning`) so live tuning keeps parity.
5. **Quantisation** — turn delay is tick-quantised the same way the step cooldown is, so server and
   client-predictor round to the same tick count (no drift).

## Constraints
- Behavior change is ONLY turn timing. Do not touch tile-step validation, held-intent handling, AOI, or
  snapshots. Default `move.turnDelayMs = 80`.
- Server + predictor must remain in lockstep (this is the crux — a mismatch reintroduces the
  rapid-direction-change snap we fixed in S56). Add/extend a predictor-parity test for the turn-delay path.
- Update `docs/protocol.md` (v18: `ServerHello.turnDelayMs`) and the movement-model doc note.
- Run `.\.shared\skills\mmo-dev\scripts\run-checks.cmd` before and after. Update the S59 turn-then-move
  tests (a turn now costs `turnDelay`, not a full cooldown): `WorldEntityMovementTests`,
  `LocalPlayerPredictorTests`, and any cooldown/parity tests. Add a test asserting a turn frees the next
  step after `turnDelay` (not after the full step cooldown).
- **Safe Local Execution** binds you. Orchestrator runs the stress gate + the human feels the turn.

## Forks: surface, don't guess
If advertising `turnDelayMs` via `ServerHello` conflicts with how the predictor currently sources the
cadence, or if the cleanest turn-eligibility mechanism needs a new field on `WorldEntity`, describe it —
don't change the held-intent model or per-entity speed (S51) behavior.

## Acceptance
- `run-checks` green. A turn frees the next step/turn after `move.turnDelayMs` (default 80 ms), not after the
  full step cooldown; whipping the cursor rotates in place without moving; settling steps at the normal
  cadence; server and predictor agree (no snap on rapid direction change). `turnDelayMs` advertised in
  `ServerHello` (v18) and live-tunable via F4. Review-request → `review/review-request-s63-free-turns.md`
  (include a fresh 120-client/30s stress run). Do NOT commit; do NOT delete the task file.
