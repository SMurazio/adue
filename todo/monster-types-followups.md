# N — monster-types follow-ups (from the P2-polish independent review)

The P2-polish pass (monster types / slime / F1 Monster tab / HP fix / red home tile) shipped SHIP-with-followups.
Two of the four flagged items have since SHIPPED and are trimmed from this file:

- ~~#1 `slime.moveSpeed` liveness~~ — **DONE.** `StepMonsterAi` now paces each monster off its TYPE's LIVE
  `MoveSpeedMultiplier`, read fresh every tick (`GameServer.cs:2719-2727`), so the F1 tab dials already-spawned
  monsters live like the other tunables.
- ~~#2 `_monsterTypeOf` despawn cleanup~~ — **DONE.** P3 despawn now calls `_monsterAi.Forget(id)` and
  `_monsterTypeOf.Remove(id)` (`GameServer.cs:1929-1931`); the former add-only leak is fixed and the `:131`
  comment matches.

The two genuinely-live items remain below.

## 3. (nit) "~312 ms" prose vs 300 ms reality
`docs/living-enemies-design.md:64` still says the slime steps "~312 ms," but tick-quantised at 20 Hz it's
exactly `round(5/0.8)=6` ticks = **300 ms**. (The `MonsterType.cs` code comment was already corrected; only the
design doc prose remains.) Cosmetic; behaviour (slower than the player) is correct. Correct the prose to ~300 ms
when convenient.

## 4. (P3 nit) Spawners are never removed
`GameServer._spawners` only grows — `/monster` adds a spawner; nothing deletes one. Fine for the phase, but a long
dev session accumulates spawners (and their markers). Add a way to clear/remove a spawner (e.g. a `/despawn` or
`/clear-spawners` admin command, or remove a spawner when its tile is re-used). Also: a downed-player harvest gap
was found + FIXED inline (InteractRequest now in IsSuppressedWhileDead) — no action needed, noted for history.
