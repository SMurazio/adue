# N — RunStatus wire hardening + guard nits (P1 review M6/L2/L5)

1. **M6 — RunStatus amplification.** Every RunReady press → `BroadcastRunStatus` → one
   reliable message to EVERY authenticated session (+ a second targeted send in
   `HandleRunReadyRequest`, GameServer.cs:~3502); the un-ready branch (RunEngine.cs:168-172)
   fires even when nothing changed. Trivial at 2 players; a 1→N reliable amplifier at
   stress-harness scale. Fix: skip the broadcast when the projected status didn't change, and
   drop the redundant targeted send.
2. **L2 — damage tally guard** (BossEncounterEngine.cs:~903) omits `_state == Active`; inert
   today (`_bossSpawned` implies Active) but the coupling is no longer by construction — add
   the explicit condition.
3. **L5 — `TryConsumeRunReadySequence` has no test** (the duo cursor has none either) — add a
   dedup/replay fact for both cursors while there.
