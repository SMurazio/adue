# NET1 — Stage 1: redundant-unreliable input delivery (server still held-paced)

PRODUCTION on `review/tile-step-todo`. **Stage 1 of the movement-netcode redesign** — read
`docs/movement-netcode-redesign-plan.md` first (this is its Stage 1, the recommended safe first step). Goal: make
the held-intent input channel **loss-robust** by switching it from `ReliableOrdered` to **unreliable + redundant**,
WITHOUT touching the server stepping model, rollback, or anti-cheat. Smallest safe surface; trivially revertable.

## What to build
1. **New `MoveInputMessage`** (`Messages.cs` + codec `ProtocolCodec.cs`), bump `ProtocolCodec.Version`:
   - `uint HeadSeq` — sequence of the newest input.
   - `bool Moving`, `Direction8 Direction` — the FULL current intent state (redundant; re-sent every packet).
   - `byte Count` + `Window[Count]` of `{ byte SeqDelta, bool Moving, Direction8 Dir }` — the last N (≈4–8) prior
     inputs as deltas, so a dropped packet's intermediate state changes are recovered from a later packet.
   - **Do NOT add authored ticks yet** — that's Stage 2 (this stage stays seq-based, server held-paced).
2. **Client send** (`MmoClient.SendMoveIntent` + the send cadence in `MmoClientRoot.cs:~1983`): send
   `MoveInputMessage` **`DeliveryMethod.Unreliable`** at a **fixed rate (~20Hz)** while moving, plus a short
   **tail after stop** (keep sending the current `Moving=false` state for ~5–8 packets) so a dropped STOP is
   recovered by redundancy — this replaces the reliable send AND the on-change/0.5s keepalive. Each new input
   takes a fresh `++_moveSequence`; maintain a small client-side ring of the last N inputs to fill the window.
   **Stop sending `MoveIntentMessage`.** Keep `MoveIntentMessage` DEFINED (it's deleted in Stage 5), just unused.
3. **Server ingest** (`GameServer` handler + `ClientSession`): on `MoveInputMessage`, walk head + window, and for
   each input with `seq > _lastMoveSeq` apply it **in sequence order** via the EXISTING `TryUpdateMoveIntent`
   (dedup: already-seen seqs dropped). `StepHeldMovementIntents` and everything downstream **unchanged** — it
   still reads the held intent, now fed from the redundant message. The wedged-client timeout stays as the
   heartbeat guard.
4. **Leave the UO-mode `StepCommitRequest` stream alone** this stage (it's superseded later). Default render mode
   must stay fully working.

## Gates + validation
- `run-checks.cmd` green + `godot-build.cmd` clean. **TEST1 (`TimingFaithfulReconcileHarnessTests`) must stay
  green UNMODIFIED** — the delivery change is invisible to the predictor harness (which already models the wire as
  latency/jitter/drop). Add: a `ProtocolCodecTests` round-trip for `MoveInputMessage` + window; a server unit test
  that dedup applies each seq once in order AND recovers a "dropped head" from a later packet's window.
- **Do NOT run `stop-mmo`/any gate that force-kills a live session** — the user may be on clumsy. If `run-checks`
  fails on a `Mmo.Shared.dll` lock, report it and leave gating to the Orchestrator (who coordinates timing). If
  `git` is denied, leave the work + `review/review-request-net1.md`.
- **Human clumsy check (put in the review-request):** default mode, **10% drop + 100ms** — today a dropped intent
  causes freeze-then-jump; after, a dropped packet recovers within one send interval (≤50ms), no stall. (UO-mode
  bunching is NOT fixed by this stage — that's Stage 4.)

## Standing rules
One discrete revertable commit referencing this task; delete this file in that commit. **Safe Local Execution**.
Revert = drop the message + restore the reliable `MoveIntent` send.

## Acceptance
The held-intent input rides an unreliable, redundant, sequence-deduped channel; a dropped input no longer stalls
or jumps the default-mode avatar (verified under clumsy); TEST1 green unmodified; gates green. Server stepping,
rollback, and anti-cheat untouched.
