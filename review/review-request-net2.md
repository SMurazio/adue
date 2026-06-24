# Review request — NET2 redundant-unreliable UO commit delivery

## Intent
Bring NET1's reliability-via-redundancy to the **UoClientDriven per-step commit stream** so it
survives packet loss. Today's per-step `StepCommitRequest`s are `ReliableOrdered`: under loss they
retransmit in a **batch** the server's cooldown gate rejects together → the local avatar speeds up +
desyncs (the GodotB symptom). NET2 ships the commits **`DeliveryMethod.Unreliable`** as a sliding
window of the last N sequenced commits, deduped by sequence — a dropped commit recovers from the
next packet's window instead of a reliable retransmit batch.

## Branch / base
- Branch: `review/tile-step-todo`
- Commit: `9acc472` (one commit; deletes `todo/NET2-redundant-unreliable-uo-commits.md`)
- Diff: `git show 9acc472` or `git diff 9acc472^ 9acc472`

## Delivery approach chosen (architectural decision)
**Interim redundant-unreliable `StepCommitBatchMessage`** — NOT folded into NET1's
`MoveInputMessage`. The todo's preferred option was folding in; I chose the explicitly-allowed
interim because the two are semantically different application paths:
- `MoveInputMessage` carries **held-intent state** applied via `TryUpdateMoveIntent` (records a held
  Moving/Direction the server-side pacer reads).
- A commit is a **one-shot step** applied via the cooldown-gated `TryCommitStep`.

Merging them means one message head feeding two different server handlers — that is the Stage 5
unification (gut `Reconcile`, collapse the model duality), too entangled for a delivery-only stage.
Keeping a parallel `StepCommitBatch` mirrors NET1 exactly (same ring/window/dedup shape), shares the
move-sequence cursor, and reverts trivially.

## Change manifest
- `src/Mmo.Shared/Protocol/MessageType.cs` — `StepCommitBatch = 11`.
- `src/Mmo.Shared/Protocol/Messages.cs` — `StepCommitWindowEntry {SeqDelta, Direction}` +
  `StepCommitBatchMessage {HeadSeq, Direction, Window}`.
- `src/Mmo.Shared/Protocol/ProtocolCodec.cs` — `Version 23 → 24`; `WriteStepCommitBatch` /
  `ReadStepCommitBatch` (bounded by `MaxStepCommitWindow = 32`, same as MoveInput).
- `src/Mmo.Client.Core/MmoClient.cs` — 8-deep commit ring (`_stepCommitRing`); `RecordStepCommit`,
  `BuildStepCommitWindow`, `SendStepCommitBatch`. The UO per-Poll burst (was one reliable
  `StepCommitRequest` per accepted step) and the model-B S103 release commit now both record into the
  ring and ship **one** `StepCommitBatch` `Unreliable`. `StepCommitRequestMessage` stays DEFINED but
  the client no longer sends it.
- `src/Mmo.Server/Runtime/GameServer.cs` — dispatch case + `HandleStepCommitBatch` walking
  head+window via the pure `ExtractFreshStepCommits` (ascending seq, dedup vs `LastMoveSeq`,
  malformed-delta drop), applying each fresh seq through the **EXISTING** `HandleStepCommit →
  TryCommitStep` at the **current server tick** (cooldown gate unchanged). The legacy
  `StepCommitRequest` handler is intact (crash-soak still drives it raw).
- `src/Mmo.Server/Properties/AssemblyInfo.cs` — `InternalsVisibleTo("Mmo.Client.Core.Tests")` so
  TEST1 can drive the REAL `ExtractFreshStepCommits` (not a reimplementation).
- `docs/protocol.md` — version line + transport bullet + message entries brought current. It was
  stale at v21; this records v22 `MovementMode`, v23 `MoveInput`, v24 `StepCommitBatch`.
- Tests: `tests/Mmo.Shared.Tests/ProtocolCodecTests.cs` (batch round-trips head+window / empty
  window; version pinned to 24); `tests/Mmo.Server.Tests/StepCommitBatchIngestTests.cs` (new;
  ordering/dedup/malformed + dedup-across-redundant-batches + dropped-head-recovers-from-window);
  `tests/Mmo.Client.Core.Tests/TimingFaithfulReconcileHarnessTests.cs` (TEST1 — new Invariant 4);
  `MmoClientUoClientDrivenTests.cs` / `MmoClientCommitStepTests.cs` updated to read the batch stream.

## Decisions / deviations
- Routed the **S103 model-B release commit** through the same ring/batch channel (not just the UO
  stream). It shares `TryCommitStep` and the same delivery concern; a single `EmitStepCommit`-style
  path keeps the wire uniform and the server to one new handler. Scope-adjacent but minimal.
- Kept `StepCommitRequestMessage` + its server handler DEFINED-but-unused (the NET1 pattern for
  `MoveIntentMessage`). This keeps `UoClientDrivenCrashSoakTests` (which sends raw
  `StepCommitRequest`) valid and makes the revert a one-liner.

## Honest scope (per the todo)
This is the **delivery** half for commits: no batching, so under **typical loss (≈3–10%)** the
commits arrive spread out, the server accepts them at cadence, and the prediction's banked steps
confirm (no bunching/desync). **Sustained heavy loss + the full latency story still need Stage 4
(authored-tick replay)** — where the server applies each commit at its authored tick via rollback so
even a backlog lands correctly. Server still applies at the current tick (authored-tick deferred).

## Self-verification
- `run-checks.cmd`: **build succeeded, 0 errors**; tests **Shared 47/47, Client.Core 271/271,
  Server 192/192** — all green. TEST1 green including the new Invariant 4.
- TEST1 Invariant 4 (`UoCommitDrop_RecoversFromRedundantBatchWindow_NoSpeedUp`): drives the
  UoClientDriven predictor, emits each accepted step as a redundant `StepCommitBatch`, drops ~30% of
  batches in a mid-run window, and asserts the server reaches the **same** tile/StepSeq as a no-loss
  run (recovery) AND `serverStepSeq == predictedStepSeq` (no speed-up — each commit applied once, at
  cadence). Uses the REAL `ExtractFreshStepCommits` + `WorldEntity.TryCommitStep`. Existing
  invariants 1–3 untouched and green.
- Standard 120c/30s stress: **NOT run** (see gaps).

## Known gaps / not done
- **`godot-build.cmd` not run** — the command was permission-denied in this session. The Godot layer
  (`Mmo.Client.Godot`) has **no** reference to the commit messages (verified by grep); it only uses
  `MmoClient`'s public API (`SendMoveIntent`/`Poll`/`RenderMode`), whose signatures are unchanged, and
  it compiles against `Mmo.Client.Core` which built clean. Expected clean; please confirm.
- **120c/30s stress gate not run** — the standard gate exercises the DEFAULT CosmeticLead path; it
  does not drive UO commits. The crash-soak (`UoClientDrivenCrashSoakTests`, still green) covers the
  UO server surface. Please run the stress gate for parity.
- **Live clumsy check not run** by me (no live session driven). See human steps below.

## Highest-risk areas to check
1. **Cooldown-floor pacing under recovered bunching.** The honest-scope claim is that recovered
   commits arrive *spread out* (each later packet re-carries the dropped seq a cadence apart). If a
   real recovery delivers several fresh commits **bunched at one server tick**, `TryCommitStep`'s
   floor rejects all but one that tick and applies the rest on later ticks — correct (no speedhack)
   but it means *sustained heavy* loss can still lag (that's the Stage-4 boundary). The TEST1 model
   applies at most one fresh commit per tick to mirror this; confirm that matches the live tick loop's
   one-commit-per-cadence reality.
2. **Shared move-sequence cursor.** Commits and `MoveInput`/`MoveIntent` all advance `LastMoveSeq`.
   A `MoveInput` head seq interleaved between commit seqs still dedups correctly (strictly-increasing
   cursor) — `MmoClientUoClientDrivenTests.UoMode_CommitSequencesAreStrictlyIncreasing` asserts this.
3. **`InternalsVisibleTo` widening** to `Mmo.Client.Core.Tests` — intentional, scoped to letting
   TEST1 call the real extractor. Confirm acceptable.

## Human clumsy check (please run)
`UoClientDriven` render mode, **10% drop + 100ms latency** (F5 net-sim): the speed-up/desync (GodotB
symptom) should be **gone** — a lost commit recovers within a send interval and the avatar tracks the
server instead of accelerating ahead. Compare against pre-NET2 (`9acc472^`) to confirm the delta.
