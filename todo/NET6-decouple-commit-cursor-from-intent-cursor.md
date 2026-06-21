# NET6 — give StepCommit its own dedup cursor (stop the keyup intent from stranding unconfirmed commits)

PRODUCTION on `main`. **PRIORITY S — this is the real root cause of the loss desync** (the trace NET5 couldn't
reach because its harness had a blind spot). Found 2026-06-21 by an independent diagnosis; was pre-flagged as
risk #2 in the old `review/review-request-net2.md`.

## Root cause (verified in code)
The client mints ONE shared `_moveSequence` for BOTH `StepCommitBatch` commits AND `MoveInputMessage` intents
(`MmoClient.cs:416` and `:687`). Server-side, `ClientSession._lastMoveSeq` is a SINGLE dedup cursor advanced by
BOTH `TryUpdateMoveIntent` (intents, `ClientSession.cs:147`) AND `TryConsumeCommitSequence` (commits, `:166`),
and both reject `sequence <= _lastMoveSeq`.

Consequence: a `MoveInput` with a HIGHER sequence than an unconfirmed commit **burns the shared cursor past
that commit**, so the server then drops every re-send of it as "already seen" (`ExtractFreshStepCommits` gates
on `HeadSeq > lastSeq`, `GameServer.cs:1729`) — it never reaches `TryCommitStepAuthored` (which WOULD accept
it; the gate is fine). Two manifestations:
- **Tail loss on stop:** keyup sends `SendMoveIntent(moving:false)` minting stop seq `N+1` (8× redundant,
  `MmoClientRoot.cs:2027`), which lands and bumps the cursor to `N+1`; the stranded tail commit `N` is then
  permanently un-acceptable → `lead` stuck at 1, NET5's re-send deduped away → the ForceResync fallback snaps.
- **Runaway while moving:** any intent update (direction change) interleaved between commits burns the cursor
  past an earlier lost commit before a later commit's redundancy window can recover it → strands accumulate →
  `lead` climbs and never catches up, faster with more loss. (Matches the live symptom exactly.)

NET5's re-send is the RIGHT mechanism — it ships the stranded commit correctly — it just can't land while the
shared cursor dedups it. This fix makes the re-send (and the normal redundancy window) actually work.

## Fix
**Decouple the commit dedup cursor from the intent dedup cursor.** Add a separate `_lastCommitSeq` on
`ClientSession`; `TryConsumeCommitSequence` rejects/advances on `_lastCommitSeq` only, `TryUpdateMoveIntent`
keeps `_lastMoveSeq` only. The client's shared monotonic `_moveSequence` is fine — the two streams just dedup
independently (commit seqs may have gaps where intents took numbers; that is OK — the gate is `seq > cursor`
and the redundancy window applies window entries oldest-first, advancing the commit cursor incrementally).
- Semantics: a commit is a one-shot "finish step N" and a late commit applying after a keyup is CORRECT (the
  step happened, then you stopped) — `TryConsumeCommitSequence` already never sets `Moving`, so decoupling does
  not reintroduce the "commit re-arms Moving after keyup" concern the shared cursor was commented for.
- Keep it minimal/local: this is a server-side dedup change. No protocol/version bump (the wire seqs are
  unchanged; only the server's bookkeeping splits). Confirm whether the keepalive-tick refresh needs to happen
  on both cursors (it currently rides `_lastMoveSeq`).

## MANDATE — fix the harness blind spot FIRST (this is why NET5 passed while live failed)
The existing `tests/Mmo.Client.Core.Tests/TailLossResendHarnessTests.cs` advances its server cursor ONLY on
commit accepts and never models a `MoveInput`/stop intent burning the shared cursor (`RunTailLoss` line ~138/168)
— so it cannot see this bug.
1. **Extend the harness (or add one) to model BOTH streams sharing one cursor**, send the keyup stop intent at
   seq `N+1` (8× redundant), and assert that on the CURRENT shared-cursor code the re-sent tail commit `N` is
   DEDUPED → `lead` stays stuck (reproduce the bug). **If you can't reproduce it, STOP and report.**
2. Then apply the separate-cursor fix → assert the re-send now lands, `lead` drains to 0, NO ForceResync snap.
3. Also model an interleaved-intent-mid-stream case (intent seq between two commits, earlier commit dropped) →
   on old code it strands; on the fix the later commit's window recovers it.
4. Regression: existing steady-walk / NET2 / NET3 / NET5 invariants stay green; intent dedup still rejects
   stale/duplicate intents.

## Gates / verify (orchestrator)
`run-checks` + `godot-build` + 120/30s stress. Live: 100ms + 1% and 3% → `lead` now drains to 0 and stays low
while moving (no runaway), no fallback snap.

## Standing rules
One discrete revertable commit referencing this task (the harness-fix may be a separate commit); delete this
file on success. Safe Local Execution; you cannot run Godot/gates. Emit `review/review-request-net6.md`.

## Acceptance
A stranded commit (tail-on-stop OR mid-stream-behind-an-intent) is now re-delivered and accepted; `lead` drains
to 0 seamlessly at ≤3% with no snap and no runaway. The harness reproduces the shared-cursor strand on old code
and proves the fix. Gates green.
