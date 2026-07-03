# N — Telegraph T2 review followups (independent review of fb08bb1: APPROVE-WITH-FOLLOWUPS)

No blockers/majors; codec symmetry, deadline-form sync, presentation-only clock, AOI diff, and Godot node
hygiene all verified sound.

1. ~~Quantize the shape AT SCHEDULE~~ **DONE**.
2. ~~Remember-known only on successful send~~ **DONE** (queue-sweep-1) — GameServer's telegraph AOI diff
   (SyncTelegraphs) and the SpawnerMarker AOI diff (SyncSpawnerMarkers, fixed the same way while there) now
   only call RememberKnownTelegraph/RememberKnownSpawner when TrySend returns true; a failed send on a
   surviving session leaves the id unknown so the diff retries next tick.
3. ~~Clear client telegraph state + clock on disconnect~~ **DONE** (queue-sweep-1) — MmoClient.Disconnect now
   clears `_activeTelegraphs` and calls the new `CosmeticServerClock.Reset()`.
4. ~~Two negative-test gaps~~ **DONE** (queue-sweep-1) — TelegraphWireIntegrationTests gained
   `ViewerOutOfInterestRadius_NeverReceivesTheTelegraph` (a genuinely out-of-AOI viewer, driven via real
   MoveIntent traffic on a narrow-interest-radius server, gets zero TelegraphMessages) and
   `KnownTelegraphIdsShrinkAfterResolve_ForgetOnResolvePin` (a headless test driven through two new internal
   test seams — `GameServer.TelegraphsForTests` / `GameServer.SyncTelegraphsForTests` — pinning that a
   session's `_knownTelegraphIds` actually shrinks once a telegraph resolves, not just grows).

NITs (still open, batch opportunistically next time these files are touched): one >2s latency spike
snap-then-snap-backs the cosmetic clock (require two consecutive out-of-band samples before re-anchoring);
the flash re-assigns MaterialOverride every frame of the 0.35s window (guard the redundant interop write).
Both skipped this batch — neither was trivial enough to fit alongside the four numbered items above without
its own verification pass.
