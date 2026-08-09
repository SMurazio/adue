# S — Run-loop symptom-level tests: exercise RespawnPlayers/MarkAlive/dead-ready for real (P1 review M4 + H1 gap)

Review M4 on 7005d15: the suite verifies the implementation's proxy flag (`IsRunParticipant`)
but never the SYMPTOM — no test constructs a `ClientSession`, so `RespawnPlayers` skipping a
run participant, `returnPlayer`'s `MarkAlive()` un-stick (GameServer.cs:~750), and the H1
dead-ready gate (GameServer.cs `HandleRunReadyRequest`, fixed post-review) are all untested.
This is the exact "test inherits the fix's model" failure mode the project contract warns
about.

Build a session-level harness (loopback/integration style — `ClearSpawnersIntegrationTests`
and `DuplicateLoginIntegrationTests` are precedents) and cover the review's named list:

- `RespawnPlayers` actually skips a dead run participant (and picks the body up the tick after
  the run ends — the defense-in-depth path).
- `MarkAlive` on run end via `returnPlayer` — no stuck-dead session after clear AND wipe.
- Readying while dead is refused (H1 gate); un-readying while dead allowed.
- One partner disconnects while the other clears; disconnect on the boss-death tick.
- Abandon with a non-empty roster; third player readying during another pair's Summary;
  `ForgetPlayer` clears a ready flag; Summary-phase `StatusFor` projection.
