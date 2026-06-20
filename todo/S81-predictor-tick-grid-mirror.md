# S81 — Implement the tick-grid mirror predictor (option A; proven in S80)

Implements the fix proven in the S80 diagnosis (`review/review-request-s80-turn-phase-lock.md` — READ IT,
it has the mechanism, the off-grid timeline, and the phase-sweep proof). Decision made (Orchestrator + human):
**option A** — mirror the server's tick grid exactly; accept ≤50 ms (~25 ms avg) first-step latency on a fresh
press in exchange for exact 0-latency tile/facing/step-seq parity through arbitrary turns. Client-only (uses
S76 wire data already present). Parity-critical.

## Why
The predictor schedules on continuous wall-clock ms while the server acts only on its 50 ms integer tick grid;
on turns they sample the held direction at different instants → diverge in tile AND step-seq (the spam-left-
right 5-6 tile gap). The proven fix makes the predictor a tick-grid mirror of `WorldEntity.TryStep`.

## What (the proven mechanism, from S80)
Rewrite `LocalPlayerPredictor`'s timing model from ms to the server tick grid:
1. **Wall-clock → serverTick calibration.** Store `(serverTickRef, receivedAtWallMs)` from snapshots
   (`MmoClient.HandleSnapshot` has `snapshot.ServerTick`; `Server.TickRate` gives `tickMs`). Map
   `now → serverTick = serverTickRef + floor((now - wallRef) / tickMs)`. **Smooth/clamp the calibration** —
   the wall clock is NTP-free and snapshot arrival jitters, so a raw per-snapshot re-base will jump; use a
   small smoothing (e.g. clamp the offset correction per update) so the estimated serverTick advances
   monotonically and doesn't stutter. (S80 flagged this as the main real-client risk.)
2. **Integer tick-grid gate**, mirroring the server: an integer `_nextEligibleTick`; on a turn
   `+turnDelayTicks`, on an accepted step `+stepCooldownTicks` and bump `PredictedStepSeq`. Process each new
   tick boundary **once, in order**, sampling the held `_direction` at that boundary (frame poll ≪ tick, so it
   matches what the server sampled).
3. **First action quantised to the next tick with `ceil`:**
   `firstActionTick = serverTickRef + ceil((pressMs - wallRef) / tickMs)` on the idle→move transition. `ceil`
   (NOT floor+1) is required for the exact-on-boundary case (S80 proved floor+1 gives 8/400 mismatches, ceil
   gives 0).
4. Convert the S77 `Reconcile` re-arm branches (currently `_nextEligibleAt = now + cadenceMs`, etc.) to tick
   arithmetic, and `AdvanceOneStep`/`SetIntent`/`Tick` to the tick model. Keep S77's step-seq reconcile +
   replay semantics intact (they were correct — they were faithfully reconciling a mis-stepping predictor).
5. **Plumbing:** pass `serverTick` + `receivedAt` (and `tickRate`) into the predictor — today `ApplySnapshot`
   has `serverTick` but doesn't hand it to `Reconcile`/the predictor (MmoClient ~line 539/828). Thread it.

## Tests (must add / update)
- **Extend the parity test OFF-GRID** (this is the gate + the proof): drive the predictor and the REAL
  `WorldEntity.TryStep` with intents arriving BETWEEN ticks, incl. rapid left-right alternation, over multiple
  press-phase × tick-phase offsets; assert tile + facing + `PredictedStepSeq` match every tick. It must FAIL on
  today's ms model and PASS after. (The current `TurnPathParity_*` is grid-aligned — that's why it missed this.)
- **Spam-left-right no-drift** predictor test reproducing the human's repro: rapid alternating off-grid intents
  then step → predicted tile does NOT accumulate divergence from the server's.
- **Update** `FirstStepFiresImmediatelyOnKeydown_NoRoundTrip`: the first step now lands on the next tick
  boundary (≤1 tick latency), not the same instant — re-express it to assert "within one tick", and update the
  "moves the instant the player inputs" design note at the top of `LocalPlayerPredictor.cs`.
- Keep all other S77 reconcile tests green.
- NOTE the irreducible sub-tick floor (S80): a frame-sampled client vs a tick-sampled server can still differ
  when the direction flips faster than the poll resolution — do NOT chase 100% on that; the structural
  accumulation is what must go to zero.

## Constraints
- Client-core only; no server/protocol change (surface + STOP if you find one is needed). Server stopped (dev
  mode). Run `.\.shared\skills\mmo-dev\scripts\run-checks.cmd` before/after (try it; if Bash denied, note +
  continue — Orchestrator runs the gate + a LIVE overlay re-test: spam left-right, green must stay on magenta).
  You can't run Godot. **Safe Local Execution** binds you. Do NOT commit, delete the task file, or push.

## Acceptance
- `run-checks` green incl. the new OFF-GRID parity test (fails on old model, passes now) + the spam no-drift
  test + updated first-step test; all S77 tests green. At 0 latency the predicted tile == server tile through
  arbitrary turn-spam (the human's bar). Review-request → `review/review-request-s81-tick-grid-mirror.md`.
  Do NOT commit or delete the task file.
