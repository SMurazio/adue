# S59 — Turn-then-move (UO movement rule): a direction change turns in place first

Severity: feature (movement feel). Implement the classic Ultima Online **turn-then-move** mechanic: a step
in a direction **different from current facing** just **turns** (updates facing, consumes the step
cooldown, no tile move); only a step in the **same** direction as facing actually **moves** one tile. This
makes rapid direction changes (esp. whipping the mouse around the character) a clean **pivot in place**
instead of the current janky zigzag, and gives the deliberate UO feel. Fixes both the mouse-rotation jank
and the keyboard rapid-direction jank — same root cause.

**Server-authoritative rule + the predictor must mirror it EXACTLY** (or prediction desyncs). This is
movement netcode — the S47b/S53 lessons apply: verify hard, the feel is the bar, revert criterion below.

## The rule (server: `WorldEntity.TryStep` + the step loop in `GameServer`)
On a step (cooldown elapsed):
- **direction != Facing → TURN:** set `Facing = direction`, consume the cooldown (set `_lastStepTick`),
  bump `StateRevision`, **do NOT change `Tile`**. (Turning is always allowed — no walkability check; you
  can face a wall.) Return a result indicating a turn occurred (a step happened, position unchanged).
- **direction == Facing → MOVE:** current behavior — if the target tile is walkable, move one tile
  (set Tile, consume cooldown, bump revision); if blocked, hold (do NOT consume the cooldown — retry next
  tick, exactly as today).
- The turn costs a **full step cooldown** (authentic UO). Put the turn cost behind a small constant/option
  so we can shorten it later if it feels too heavy (default = full cooldown).

Net: changing direction = one cooldown to turn, then moves; holding a direction you already face = moves
immediately (no turn). Starting movement from idle in a non-facing direction costs one turn first.

## Predictor mirror (`LocalPlayerPredictor.Tick`)
Same rule, deterministically identical to the server so prediction matches:
- direction != facing → turn (update facing, advance the step schedule by one cadence, no tile change, do
  NOT feed the interpolator a move).
- direction == facing → move (current predicted step: walkable → advance tile + feed interpolator; blocked
  → hold without consuming, as today).
Reconcile is unchanged (server facing+tile in snapshots; both sides apply the same rule → stay in sync).

## Test impact (expect to update several)
Many movement/AOI/harvest tests assume "one step = one tile moved." With turn-then-move, the FIRST step in
a direction the entity isn't already facing now TURNS (no move). Default facing is `Direction8.S`.
- Update affected tests: either pre-face the entity, or account for the extra turn step (e.g. step twice,
  or assert the turn then the move). Do NOT weaken what they verify — adjust for the new, correct behavior.
- Add a shared test helper if it reduces churn (e.g. "walk N tiles in a direction" that accounts for the
  initial turn).
- The synthetic stress clients (random direction every ~1s) will now turn-then-move — fine, they still
  roam; the stress gate should still pass.

## New tests
- `WorldEntity`: a step in a new direction turns (Facing changes, Tile unchanged, cooldown consumed,
  revision bumped); the next step in that direction moves; a blocked move still holds without consuming.
- Predictor: turn-then-move matches the server rule (a direction change is a turn, not a move); rapid
  direction flips produce turns (no tile change) not zigzag moves; no-divergence steady state still fires
  no correction.

## Files
- `src/Mmo.Server/Runtime/WorldEntity.cs` (+ `GameServer` step loop / `MovementStepResult`),
  `src/Mmo.Client.Core/LocalPlayerPredictor.cs`, and the affected tests.

## Acceptance
- Whipping the mouse around the character → it **pivots in place, doesn't janky-zigzag** (UO feel); rapid
  keyboard direction changes likewise turn cleanly; normal walking unchanged (you move immediately in the
  direction you face). `run-checks` green (tests updated for the new rule), `godot-build` green, the
  120c/30s stress still healthy. Human feel sign-off. **Revert criterion:** if turn-then-move feels too
  heavy/sluggish, shorten the turn cost (the tunable) or revert — it's a feel experiment.
