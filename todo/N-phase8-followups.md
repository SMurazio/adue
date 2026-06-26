# Phase 8 (continuous monster AI) follow-ups

## A — delete the dead tile-step path (sub-commit 6, deferred — fork)
After Phase 8 the monster AI hops; `Zone.TryStep`/`WorldEntity.TryStep`/`IsStepWalkable` (S75 corner-cut)/
`MovementStepResult` have NO production caller. BUT they're still the TEST DRIVER for ~7 non-monster suites
(incl. `WorldEntityCombatTests`' player attack-movement-root invariant). Deleting them needs those tests
migrated to a surviving primitive (drive player movement via `IntegrateMovement`/a test helper instead of
`TryStep`). Do that migration, then delete the tile-step path. Independent + reversible; the feature works
without it.

## B — retire the legacy `MonsterType.AttackRange` (minor)
`MonsterType.AttackRange` (int, Chebyshev) is now legacy — the continuous AI reads `AttackRangeUnits` (1.5).
`AttackRange` is kept only for the monster-tuning wire/registry/F1 display. When the monster-tuning wire is
next revisited, retire `AttackRange` (or replace the F1 display with `AttackRangeUnits`).

## C — HopDistanceUnits/AttackRangeUnits live-tunability (optional)
They're per-type fields read fresh but not live AdminSetTuning keys (would add MonsterTuningSnapshot wire
fields + F1 UI). Add if live-tuning the hop distance / attack range becomes useful.

# Phase 10 (persistence) follow-up

## D — make the new-character pos explicit on INSERT (minor)
Phase 10 fixed "new char loads at (0,0)" by setting the `pos_x`/`pos_y` column DEFAULT to 8 (mirroring the
spawn-tile default), so a defaults-only INSERT lands at the spawn centre. This couples the pos default to the
tile default (both 8). Cleaner: have the create/upsert INSERT set `pos_x = tile_x`, `pos_y = tile_y` explicitly
(or from the actual spawn point) rather than relying on a matching column default. Low priority; works today.

## E — strengthen 3 authored attack-root tests (very minor, Phase 11 review)
The 3 AUTHORED anti-cheat tests (`AuthoredAttackRootAnchorsOnAuthoredTickNotReceiveTick` + the two clamp tests in
`WorldEntityCombatTests`) now assert only the boolean `IsMovementFrozen` (they pin the window arithmetic). The
consequence (frozen => no Position change) is proven once in the core `AttackMovementRootDelays...` test, so coverage
is not lost. Belt-and-suspenders: route those 3 through `IntegrateIfNotFrozen` + a Position assertion at an in-window
tick. Very low priority.
