# Review request — UO5: frame-drop overshoot never reconciles (UO client-driven)

## Intent
Fix the live bug: a lag spike / frame drop in `UoClientDriven` mode teleports the predicted (green) tile
far forward and it NEVER reconciles — predicted stays stuck far ahead of confirmed (magenta), permanently.
Reproduces easily at 50 ms. Acceptance: a frame-drop overshoot ALWAYS converges back to the server within a
bounded time, AND the UO3 release behaviour is preserved (no backward snap on a normal release).

## Branch / base
- Branch: `review/tile-step-todo`
- Base commit: `3c9a2f08fa23d0ec04cc041cd28690316b3740fc`
- Diff: `git diff 3c9a2f08 -- src/Mmo.Client.Core/LocalPlayerPredictor.cs tests/Mmo.Client.Core.Tests/LocalPlayerPredictorTests.cs`

## Confirmed mechanism (file:line)
1. `LocalPlayerPredictor.Tick` (`src/Mmo.Client.Core/LocalPlayerPredictor.cs:405`): the catch-up loop banks up to
   `MaxTicksPerCall=8` accepted steps in ONE frame on a lag spike. In `UoClientDriven` each accepted step emits a
   `StepCommitRequest` (`src/Mmo.Client.Core/MmoClient.cs:303-309`, capped at `UoCommitBurstCap=8`) — so one laggy
   frame fires a BURST of commits.
2. Server `WorldEntity.TryCommitStep` (`src/Mmo.Server/Runtime/WorldEntity.cs:269+`) gates commits with
   `CommitAcceptFraction=0.5` (`GameServer.cs:40`) and the no-speedhack borrow: it accepts at most ~one commit per
   half-cooldown; the burst's later commits are REJECTED with `commit_too_early`, so `StepSequence` / the
   recipient-scoped `RecipientStepSeq` does NOT advance for them.
3. `Reconcile`'s `_clientDriven` branch (`LocalPlayerPredictor.cs`, pre-fix ~line 587) sized in-flight as
   `rawInFlight = PredictedStepSeq - serverStepSeq` with `cap = InFlightDirCapacity (32)` — effectively uncapped (UO3
   removed the `MaxInFlightLead=2` cap on purpose, to hold for genuine banked commits on release). So the rejected
   overshoot re-projects forward on every snapshot FOREVER — `serverStepSeq` is stuck, the lead never drains → the
   permanent overshoot the user saw. The S83 comment is explicit the cap is load-bearing ("without it the gap grows
   unbounded"); UO3 traded it for the full hold, which is correct for a draining stream but wrong for a rejected one.

## Fix
Distinguish a GENUINE banked stream (drains: `serverStepSeq` advances toward the head every snapshot) from a REJECTED
overshoot (stalls: `serverStepSeq` stops advancing while a positive lead persists), using a stall counter in
`Reconcile`:
- New state `_prevServerStepSeq` / `_hasPrevServerStepSeq` / `_stallReconciles`, const `OverPredictStallLimit = 2`.
- On each confirming reconcile: if `serverStepSeq` equals the previous one AND a positive lead remains, increment the
  stall counter; otherwise reset it to 0.
- While `_clientDriven` and NOT stalled: hold the full count (cap `InFlightDirCapacity`) — UO3 unchanged.
- Once `_stallReconciles >= OverPredictStallLimit`: fall back to `cap = MaxInFlightLead (2)`, pulling the overshoot
  DOWN toward the server. Because Reconcile sets `_predictedStepSeq = serverStepSeq + inFlight`, the seq is also
  pulled into the bounded window, so it can't re-run away.

### Why this preserves UO3
The normal release DRAINS: every snapshot confirms one more banked commit, so `serverStepSeq` advances 1→2→3…, which
resets the stall counter to 0 every time — the full-count hold path is taken, the head holds forward and settles onto
the banked destination (no backward snap). Only a stream that fails to drain for two consecutive snapshots (the
rate-limit-rejected frame-drop burst) trips the guard. One stall is tolerated as normal jitter (a 50 ms snapshot
window can legitimately confirm zero new commits). Convergence is bounded: ~`OverPredictStallLimit` snapshots
(~a few × 50 ms). The server-paced / cosmetic / genuine-reject paths are untouched.

## New tests (both pass)
`tests/Mmo.Client.Core.Tests/LocalPlayerPredictorTests.cs`:
- `ClientDriven_FrameDropOvershoot_ServerStepSeqStalls_ConvergesBackToServer` — one Tick at t=750 banks 6 E steps
  (the frame-drop burst); `serverStepSeq` stalls at 1 across snapshots; the head holds for the first two confirms
  (1 stall tolerated), then on the second consecutive stall (>= limit) the prediction is pulled back from the stale
  (6,0) to confirmed+capped = (3,0). The permanent overshoot is broken within a bounded time.
- `ClientDriven_ReleaseDrains_ServerStepSeqAdvances_HoldsForward_NoFalsePullback` — a draining release (serverStepSeq
  1→2→3→4) keeps the head pinned at the banked (4,0) the whole way (all `Matched`), proving the UO5 guard does NOT
  false-trip on the UO3 release.

## Self-verification evidence
- `Mmo.Client.Core` builds clean (0 warnings / 0 errors).
- `LocalPlayerPredictorTests`: 44/44 pass (incl. all preserved UO3/UO4 tests + 2 new UO5 tests).
- Full `Mmo.Client.Core.Tests`: 237/237 pass.

## Known gaps / what the reviewer should run
- **The standard full gate (`run-checks.cmd`) and `godot-build.cmd` were NOT run by me**: the live server holds
  `Mmo.Shared.dll` (`.NET Host` PID 26452 — the user may be playing), and the solution build cannot copy that DLL
  without killing the server, which the task forbids. The Orchestrator should run the full `run-checks.cmd`
  (and `godot-build.cmd`) once the session is free, plus the standard 120-client / 30 s stress run.
- No git commit was made by me (left to the Orchestrator per timing). The fix + tests are in the working tree; this
  request and the `todo/UO5-*.md` deletion should land in the commit.
- Highest-risk area to eyeball: the live tuning of `OverPredictStallLimit = 2`. At very high real RTT the genuine
  banked stream could momentarily confirm zero commits in a single snapshot window (1 stall) — tolerated — but two
  consecutive zero-confirm windows with a persistent lead would trip the guard and pull a still-genuine lead down to
  2 before resuming. That is a graceful, bounded correction (no overshoot), but if it visibly clips a high-RTT
  release the limit can be raised. The human should verify the live 50 ms repro converges and a normal release still
  lands smoothly.
