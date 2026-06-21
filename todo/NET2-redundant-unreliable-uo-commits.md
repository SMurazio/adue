# NET2 — redundant-unreliable UO commit delivery (loss-robust UO mode)

PRODUCTION on `review/tile-step-todo`. Bring NET1's loss-robustness to the **UO commit stream** so
`UoClientDriven` survives packet loss. User confirmed UO is the keeper render (feels right at high latency) and
wants it loss-robust. Today the per-step `StepCommitRequest`s are `ReliableOrdered` → under loss they retransmit
in a **batch** → the server's cooldown gate rejects the batch → the local avatar **speeds up + desyncs** (the
GodotB symptom). Fix = the same trick NET1 used for held-intent, applied to commits.

## What to build
Mirror NET1's redundant-unreliable pattern for the commit stream:
- Send the UO step-requests **`DeliveryMethod.Unreliable`** as a **sliding window of the last N sequenced
  commits** (dedup by sequence), so a dropped commit is recovered from the next packet's window (~50ms late,
  spread out) instead of a reliable retransmit batch.
- **Preferred (forward-compatible):** carry the commit window inside the NET1 `MoveInputMessage` (it already
  carries a held-input window + the shared `_moveSequence` cursor) rather than a second message — this is the
  direction the unified end-state goes (`docs/movement-netcode-redesign-plan.md` Stage 5). If folding it in is
  too entangled for this stage, a redundant-unreliable `StepCommitRequest` window is an acceptable interim.
- **Server:** dedup commits by sequence; apply each fresh one through the **EXISTING `TryCommitStep`** (current
  server tick, cooldown gate). **Do NOT change to authored-tick application yet** — that's Stage 4. This stage is
  the *delivery* half for commits, nothing more.

## Honest scope (state in the review-request)
This makes the commits arrive spread-out instead of batched, so the server accepts them at cadence and the
prediction's banked steps confirm (no bunching/desync) under **typical loss (≈3–10%)**. **Sustained heavy loss
and the full latency story still need Stage 4 (authored-tick replay)** — where the server applies each commit at
its authored tick via rollback so even a backlog lands correctly. NET2 is the focused UO loss win; Stage 4
completes it.

## Gates + validation
- `run-checks.cmd` green + `godot-build.cmd` clean. **TEST1 must stay green** (extend it only if you add a
  UO-commit-loss assertion; do not weaken existing invariants). Add a codec round-trip + a server dedup/recovery
  unit test for the commit window.
- **Do NOT run `stop-mmo`/any gate that kills a live session.** If `run-checks` hits a `Mmo.Shared.dll` lock,
  report it and leave gating to the Orchestrator. If `git` denied, leave work + `review/review-request-net2.md`.
- **Human clumsy check:** `UoClientDriven`, **10% drop + 100ms** — the speed-up/desync (GodotB symptom) should be
  gone; a lost commit recovers within a send interval and the avatar tracks the server.

## Standing rules
One discrete revertable commit referencing this task; delete the todo in it. **Safe Local Execution**.

## Acceptance
UO-mode step commits ride a redundant-unreliable, sequence-deduped channel; UoClientDriven no longer speeds-up/
desyncs under typical packet loss (verified under clumsy); TEST1 green; gates green. Server still applies at
current-tick (authored-tick replay deferred to Stage 4).
