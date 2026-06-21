# S106 — live speed DROPDOWN (unnamed, discrete tick-quantized speeds)

PRODUCTION on `review/tile-step-todo`. Add an F6 **dropdown** to select the local player's movement speed live,
for feel-testing. User requirement: **NOT named brackets** (no "Walk/Run") — a dropdown of a bunch of selectable
speeds shown by their **numeric values**, that the user can scrub through.

## Reuse (this is mostly UI — the speed path already exists)
- Per-entity speed already works live: the `/speed <multiplier>` command → `WorldEntity.TrySetSpeedMultiplier`
  (GameServer.cs ~1304) sets `SpeedMultiplier`, and `BroadcastMovementSpeedChanged` → `MovementSpeedChangedMessage`
  (GameServer.cs ~1353) tells the client the new `stepCooldownMs` so prediction/glide re-syncs. The dropdown just
  drives that path with chosen multipliers. Do NOT touch `move.stepCooldownMs` (that's the GLOBAL base in F4) —
  this is the LOCAL player's per-entity speed.

## What to build
1. An F6 movement-panel **OptionButton (dropdown)** "Move speed", **always shown** (speed is mode-agnostic, like
   Net latency — per the UO2 contextual logic). Populate it with the discrete tick-quantized speeds N = 1..8
   ticks. For each N, the multiplier that yields exactly N ticks is `baseWalkTicks / N` (walk = 3 ticks at the
   140ms default → N=3 is 1.0×). Label each option by its **numbers only** — e.g. `3.00× · 50 ms · 20.0/s`,
   `1.50× · 100 ms · 10.0/s`, `1.00× · 150 ms · 6.7/s` (default/walk), `0.75× · 200 ms · 5.0/s`, … `0.38× · 400 ms
   · 2.5/s`. (Compute cadence = N×(1000/tickRate); tiles/s = 1000/cadence.) Mark/preselect the current walk (N=3,
   1.0×).
2. On select, set the local player's speed live via the existing per-entity path (send `/speed <mult>` through the
   existing chat-command send, like the `/replay` toggle — or a dedicated `MmoClient` method if cleaner). No
   restart.
3. **Verify the client prediction/glide actually re-syncs** to the new cadence on a live change, in ALL render
   modes (Predicted / CosmeticLead / AcceptDeny / UoClientDriven) — `MovementSpeedChangedMessage` must feed the
   predictor's AND the cosmetic driver's cadence, including a mid-move change (no desync/snap on the speed
   switch). If a driver doesn't pick up the new cadence, fix that (it's the real meat of this task).

## Notes / scope
- Range N=1..8 is a starting set; if the min/max effective-cooldown clamp (`MinEffectiveStepCooldownTicks` /
  `MaxEffectiveStepCooldownTicks`, GameServer.cs ~1537) rejects an extreme, clamp the dropdown to the allowed
  range and note it (don't offer a speed the server will refuse).
- This is a TESTING/tuning control. Gameplay speed assignment (mounts, etc.) is out of scope — just the live
  selector.

## Gates
- `run-checks.cmd` green + `godot-build.cmd` clean. A test where feasible (the multiplier→cadence label math; the
  client picks up `MovementSpeedChangedMessage` cadence). If your shell is denied, say so and do NOT claim green —
  the Orchestrator runs the gates (and will coordinate timing so it doesn't stop a live session).

## Standing rules
One discrete revertable commit referencing this task; delete this file in that commit. **Safe Local Execution**.
You cannot run Godot — the human verifies the dropdown live.

## Acceptance
F6 shows a "Move speed" dropdown of unnamed numeric speeds; selecting one changes the local player's speed live
and the avatar's prediction/glide tracks the new cadence in every render mode (incl. mid-move). Gates green.
