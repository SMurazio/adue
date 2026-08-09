# S — Run-loop edge semantics + legibility (P1 review M2/M3/M5/M7/L1/L6)

From the independent review of 7005d15 (SHIP-WITH-FOLLOWUPS). One task — these all live in the
same seams (RunEngine ready/refusal/end paths + GameServer wiring):

1. **M2 — location-blind roster freezes a leaver dead in town.** `IsRunParticipant`
   (RunEngine.cs:151) ignores location; `RespawnPlayers` skips on it alone. A participant who
   `/boss`-leaves the arena alive stays rostered; dying in town then freezes them dead for the
   whole remaining run ("You are down. No respawn until the run ends." — in town). Fix: leaving
   the arena alive mid-run = leaving the run (drop from roster + notify), or scope the respawn
   skip to bodies inside the arena.
2. **M3 — StartRun's refusal path reports success and wipes unrelated ready state.**
   RunEngine.cs:322-335: on `_beginBossRoom` refusal, TryReady still returns true with "the run
   begins" AND clears the GLOBAL `_ready` set. Return a real failure, keep untouched players'
   ready flags, and make the refusal the only message.
3. **M5 — abandon with a live roster is silent.** `EndRun(Abandoned)` (RunEngine.cs:357-362)
   sends no notify/summary and teleports to the town anchor instead of captured return tiles.
   Emit a summary (or at least a notify line) and use the captured return positions.
4. **M7/L1 — Summary phase is globally dismissible.** Any player readying (161) or un-readying
   (159-172) during another pair's Summary dismisses their end screen. Minimal fix at 2-player
   scale: only roster members can end the Summary early.
5. **L6 — encounter victory text still says "Leave with /boss." inside a run.** Suppress or
   reword when a run is active.

Acceptance: headless tests per item in RunEngineTests style (the refusal test must assert the
MESSAGE, not `out _` — the review caught that gap).
