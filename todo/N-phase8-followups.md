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
