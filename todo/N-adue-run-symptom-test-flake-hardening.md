# N — Harden the run-loop symptom test against timing flake (from the P1-followups review)

Non-blocking follow-ups from the independent review of the P1 run-loop followups batch
(verdict SHIP-WITH-FOLLOWUPS). The batch shipped; these harden it.

1. **[LOW — test robustness] `tests/Mmo.Server.Tests/RunLoopSessionIntegrationTests.cs`
   (`DeadRunParticipantStaysDownInArenaUntilRunEnds_...`, the ~1000ms in-arena watch loop).**
   The watch is a WALL-CLOCK loop (`DateTimeOffset.UtcNow` + `await Task.Delay(20)`) that relies on
   the freshly-spawned boss being unable to close on Bravo and force an early wipe — which would
   teleport Alpha out of the arena and cause a FALSE failure on the `ContainsInterior(a.OwnTile)`
   assert. Margins are generous today (300ms respawn vs 1s watch; boss ~10 tiles away with telegraph
   windups), but under heavy CI load `Task.Delay` can stretch the watch and give the boss more time —
   a latent flake vector once real CI exists. Fix: end the watch on a deterministic tick/poll count
   (or explicitly park/neutralise the boss for the window) instead of trusting wall-clock chase
   distance. Also add an assert or comment pinning the SILENT invariant the test depends on:
   `IssuerEntryTile`/`PartnerEntryTile` must be >1 tile apart so Alpha's radius-1 `/slam` never also
   hits Bravo.

2. **[INFORMATIONAL — no fix needed unless N scales] `src/Mmo.Server/Runtime/RunEngine.cs` (the
   successful-start path, ~`_ready.Clear()`).** On a SUCCESSFUL run start, the global `_ready` set is
   cleared, wiping any unrelated waiting player's ready flag. Pre-existing, harmless in a strictly
   2-player game, and `BroadcastRunStatus` re-syncs the affected client so there is no drift. Noted
   only for completeness (M3 tightened the sibling *refusal* path but left this one). If the game ever
   supports >2 concurrent lobby players, scope this clear to the starting pair like M3 did.
