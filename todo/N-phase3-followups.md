# Phase 3 (continuous wire) review follow-ups

From the Phase 3 Pass B independent review (commit `899200c`, verdict SHIP-WITH-FOLLOWUPS). Non-blocking; the code
is correct — these guard against regression + polish.

## A — server raw-direction-normalize guard test (RECOMMENDED, do in/around Phase 4)
`GameServer.HandleMoveIntent` normalizes the client's raw `MoveIntent` direction (`rawDir.Normalized()`) BEFORE
integrating — this is the load-bearing line that stops a hostile raw `(1,1)` (√2 boost) or `(100,0)` (magnitude
boost) from becoming a SPEED EXPLOIT. **No test currently exercises it on the server path** — the only diagonal-speed
test (`WorldEntityIntegratorTests`) feeds an already-unit vector, and every server-integration helper sends
pre-normalized cardinals. So a future refactor dropping the normalize would leave EVERY test green while reopening
the exploit. **Add a server-path test:** send a raw non-unit `MoveIntent` (`DirX=1,DirY=1` and `DirX=10,DirY=0`) and
assert the integrated distance equals the cardinal `(1,0)` case (speed neither boosted nor throttled by magnitude).
Natural to add during Phase 4 (which touches the client send + the predictor).

## C — idle client send-gate (minor, later)
The Godot client sends a `(0,0)` `MoveIntent` every render frame while standing still (~60 Hz) — a constant
standing-still packet stream. The server dedups by seq and the dt-budget makes it harmless, but it's wasteful vs the
old v35 stop-tail. Add an idle send-gate (don't send while dir is zero and already-stopped). Not urgent.

## B — DONE
Stale `AssemblyInfo.cs` comment referencing the deleted `GameServer.ExtractFreshStepCommits` — refreshed in the
Phase 3 follow-up commit.
