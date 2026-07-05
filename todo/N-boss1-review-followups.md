# N — BOSS-1 review follow-ups (LOW/NIT from the independent review of a221148)

The MEDIUM (post-victory straggler soft-lock) was fixed immediately (victory-eject grace window).
Remaining, none blocking:

- **LOW — skillshot killing blow detects victory one tick late.** TickCore order is telegraphs →
  boss encounter → skillshots; a duo-skillshot kill lands after the encounter's Step, so the
  fanfare arrives next tick (50ms, cosmetic). If BOSS-2 hooks fusion events into the encounter,
  revisit the ordering then rather than special-casing now.
- **LOW — RemotePositionInterpolator TeleportSnapUnits=8 vs charge ceiling.** Today's content is
  safe (sunderer charge 8u/300ms across ~10Hz sends stays under threshold), but
  MonsterTypeRegistry.MaxChargeDistanceUnits=16 is live-tunable: a type retuned near the ceiling +
  one dropped snapshot could trip the teleport snap on a normal charge. Whoever next retunes
  charge distances must check this margin (or scale the threshold off the registry max).
- **NIT — monsters.json sunderer comment** says "roamRadius 0 = never wanders" but the value is
  0.5 (the registry clamp floor). Fix the comment when next touching the file.
- **NIT (orchestrator-spotted, pre-existing) — duplicate xUnit theory row**:
  FreeAimSectorResolverTests.SharedHelperReproducesResolverHitMiss(targetX:10, targetY:11, aim:0,
  expectedHit:false) appears twice; xUnit skips the dupe. Delete one row.
