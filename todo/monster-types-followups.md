# N — monster-types follow-ups (from the P2-polish independent review)

The P2-polish pass (monster types / slime / F1 Monster tab / HP fix / red home tile) shipped SHIP-with-followups.
Three non-blocking items the reviewer flagged:

## 1. `slime.moveSpeed` doesn't apply to already-spawned monsters (liveness inconsistency)
Every other knob on the Monster tab edits live, but `MoveSpeedMultiplier` is copied to `entity.SpeedMultiplier`
ONCE at spawn (`GameServer.cs` ~:1557) and the cadence reads the entity value via `EffectiveStepCooldownTicks(entity)`
— so editing "move speed" in the tab affects only NEW spawns. Surprising (9 live, 1 not).
**Fix (pick one):** (a) compute the monster's step cadence from its TYPE's live `MoveSpeed` in `StepMonsterAi`
(consistent with how the other Tunables are read fresh each tick — the cleanest; keep the `SlimeMoveSpeed…` test's
`6u` expectation); or (b) on a `<type>.moveSpeed` admin change, re-apply `SpeedMultiplier` to every spawned monster
of that type. Then it dials live like the rest.

## 2. `_monsterTypeOf` cleanup on despawn (lands with P3) + stale comment
`GameServer.cs:131` comments that `_monsterTypeOf` is "cleared on despawn (Forget)", but nothing removes from it
and `MonsterRoamAi.Forget` is never called (monsters have no despawn path pre-P3) — so NO leak today, but it
becomes one the moment **P3 adds monster death/despawn**. When wiring P3 despawn: remove from `_monsterTypeOf`
AND call `_monsterAi.Forget(id)`. Until then, correct the comment to say "add-only until P3 despawn."

## 3. (nit) "~312 ms" prose vs 300 ms reality
The doc + `MonsterType.cs:16` + some comments say the slime steps "~312 ms," but tick-quantised at 20 Hz it's
exactly `round(5/0.8)=6` ticks = **300 ms**. Cosmetic; behaviour (slower than the player) is correct. Correct the
prose to ~300 ms when convenient.
