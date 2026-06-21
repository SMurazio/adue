# UO5 — prediction teleports forward and NEVER reconciles (UO mode) — frame-drop AND packet-loss

PRODUCTION on `main`. **PRIORITY S — correctness/feel bug. Reproduces LIVE under 100ms latency + 10% packet
loss (user, 2026-06-21, forced via clumsy): the avatar "speeds up + desyncs", predicted (green) tile strands
far ahead of confirmed (magenta), static, and DOES NOT RECOVER.** Originally found via frame-drop; the same
uncapped-hold root is now confirmed loss-triggered.

## UPDATE 2026-06-21 — NET1–3 shipped; bug STILL reproduces under loss; fix narrowed to the bounded hold
The movement loss-robustness milestone shipped to `main`: **NET1** (held-intent input → redundant-unreliable),
**NET2** (UO commit stream → redundant-unreliable), **NET3** (authored-tick command processing — the server
applies a recovered commit at its AUTHORED tick so the cooldown gate ACCEPTS it instead of rejecting). NET3's
headless loss-invariant passes. BUT the live desync under 100ms + 10% loss is UNCHANGED, because:

- NET3 only helps commits that ARE **recovered** (delivered in the redundant window, then accepted). It does
  **nothing** for confirmation that is genuinely **lost on the wire** — a commit never delivered, OR a
  server→client snapshot/`RecipientStepSeq` confirm that drops (the confirm path has had NO loss-hardening).
- When confirmation stalls for ANY reason, **UO3's still-uncapped hold re-projects the full
  `PredictedStepSeq - serverStepSeq` lead forward forever** → the overshoot never drains → permanent desync.
  This is the SAME root as the frame-drop case below; loss is just another way to stall confirmation.

So the remaining fix is **candidate #1 only — the cadence/latency-bounded hold** (see "Fix" below): hold up to
~`ceil(RTT/cadence) + margin` tiles of in-flight and converge anything beyond it toward the server. This
converges the overshoot **regardless of WHY confirmation stalled** (lost commit, lost confirm, OR rejected
burst), so it is robust to packet loss by construction. Do NOT add more redundancy/retransmit — the hold is
the bug.

### Mandate for this attempt (disciplined, given 3 prior misses — UO5-stall-counter, NET2, NET3-live)
1. **REPRODUCE HEADLESS FIRST**, in the TEST1 timing-faithful harness
   (`tests/Mmo.Client.Core.Tests/TimingFaithfulReconcileHarnessTests.cs`): a new loss-invariant at **100ms
   latency + 10% drop**, dropping packets in **BOTH** directions (client→server commits that fall outside the
   recovery window so they're never confirmed, AND server→client snapshots/confirms). Assert the
   predicted-vs-server step-seq **stays permanently split** (the bug) on the current code. **If it cannot be
   reproduced headlessly → STOP and report** — that means the mechanism is the confirm path or something the
   harness still doesn't model, and we instrument live instead of guessing a fix.
2. **Then fix** with the bounded hold, red→green against that loss-invariant (the split must CONVERGE within a
   bounded time).
3. **Regression guard (the exact thing the reverted stall-counter broke):** the steady-walk invariant
   (normal 50–100ms walk, no loss) MUST stay green — **no caps, no snaps** on a legit RTT lead. Verify a live
   100ms+10% run converges AND a live 100ms no-loss walk never snaps.

### ⚠️ RE-SCOPED + DEFERRED — this is TIER 2 ONLY, not the tier-1 recovery fix
The bounded hold is a client-side **mask** (snap the prediction back when it strands) — it is the **tier-2
forced-resync safety for 4–6% loss**, NOT smooth tier-1 recovery. Tier-1 recovery (0–3%, no visible snap)
requires the lost step to actually get **confirmed** (recovery-chain links 2+3 — server pace-not-reject +
client re-base), which is a SEPARATE fix. See `docs/movement-loss-degradation-tiers.md`.
- **DO NOT pick this up yet.** Order: **RESYNC1 → DIAG1 (measure the stuck link) → tier-1 recovery fix →
  THEN this (tier 2).** DIAG1 may also change what the bounded hold needs to do.
- When it IS done: use the RESYNC1 `ForceResync()` primitive for the tier-2 snap (don't reinvent it); validate
  TEST1 at **4–6% drop → forced resyncs occur, bounded, connection maintained, recovers**; the steady-walk +
  low-loss (≤3%) invariants MUST stay green (no forced snap there — that's tier 1's job).
- Tier 3 (6%+, reconnect/resync) is NET4.

---

## Original frame-drop write-up (root mechanism — still accurate)
User: a lag spike / frame drop made the avatar "teleport way forward and never reconciled" — the predicted
(green) tile sits far ahead of the confirmed (magenta) tile, static, permanently.

## Confirmed mechanism (verify, then fix)
1. On a frame drop, `LocalPlayerPredictor.Tick`'s catch-up loop banks up to `MaxTicksPerCall` (8) accepted steps
   in ONE call → the prediction jumps forward several tiles at once, and in UO mode emits a BURST of
   `StepCommitRequest`s (one per accepted step, up to `UoCommitBurstCap`).
2. **UO3 uncapped the in-flight re-projection for client-driven** (`_clientDriven` path: counts
   `PredictedStepSeq - serverStepSeq` and re-projects it ALL, NOT capped by `MaxInFlightLead=2`). The S83
   `Reconcile` comment is explicit the cap is load-bearing: *"without it… the gap grows unbounded."* UO3 removed
   it on purpose to hold for banked commits (fixing the release snap).
3. The server can't confirm a burst: `WorldEntity.TryCommitStep`'s cooldown gate + no-speedhack borrow accept ~one
   step per cooldown, so the rapid burst's later commits are REJECTED (the server's `RecipientStepSeq` doesn't
   advance for them). So the uncapped in-flight (the overshoot) NEVER drains → the prediction is stuck far ahead
   forever. That's the "never reconciles."

## FIRST ATTEMPT REVERTED (commit 9cf3abf → reverted 1bb3ad6) — it made everything WORSE
The first fix added a per-snapshot "stall counter": after `OverPredictStallLimit=2` consecutive snapshots where
`serverStepSeq` did NOT advance while a lead persisted, fall back to the bounded `MaxInFlightLead=2`. **This
mis-fired during NORMAL play → constant snapping, much worse than the original bug.** Suspected cause: snapshots
arrive ~20Hz (50ms) but a step CONFIRMS only every cadence (~3 ticks / 150ms), so during ordinary walking
`serverStepSeq` naturally does not advance on ~2 of every 3 snapshots — tripping the stall counter and
capping/snapping a perfectly legit RTT lead. **A per-snapshot stall count fundamentally cannot tell "normal
between-confirm snapshots" from "rejected overshoot" — do NOT reuse it** (this kills candidate #2 below).

The next attempt must use a discriminator that respects the confirm cadence and leaves normal 50–100ms walking
COMPLETELY untouched (no caps/snaps), acting only on a true runaway: e.g. bound the lead by a CADENCE/LATENCY-
derived tile count directly (`~ceil(RTT/cadence) + margin`, independent of per-snapshot advance), or stall over
real TIME ∝ cadence (not snapshot count), or key off the server actually signalling `commit_too_early` rather
than inferring rejection from seq non-advance. Verify against a live 50ms walk that nothing snaps.

## Fix (investigate + choose; do NOT break the UO3 release fix)
The tension: UO3 must HOLD for genuinely-banked-and-will-confirm commits (the release case: a small, ~RTT-worth
in-flight that the server WILL accept) but must NOT hold an overshoot the server is REJECTING (the frame-drop
burst: large, rate-limit-rejected). Distinguish them. Candidate approaches (pick what the code/repro supports):
- **Latency-derived bound on the UO hold:** hold up to ~RTT-worth of in-flight (+ small margin), correct the
  excess beyond that toward the server. The release in-flight fits under it; a frame-drop burst exceeds it and is
  pulled back. (Replaces UO3's fully-uncapped re-projection with a dynamic cap, not the fixed 2.)
- **Stale/rejected-in-flight detection:** if `serverStepSeq` stops advancing toward `PredictedStepSeq` for N
  snapshots (the banked commits were rejected, not just in flight), reconcile DOWN instead of holding.
- **Don't over-bank in UO mode:** cap/space the catch-up burst so the client doesn't predict further than the
  server can validate at cadence (and/or have the server apply commits at their authored `ClientTick` so a
  cadence-spaced burst confirms — note this is the parked authored-tick-replay idea; only pull it in if needed).

Whatever you choose: a frame-drop overshoot must ALWAYS converge back to the server within a bounded time, AND a
normal release must still land smoothly without the backward snap UO3 fixed. Add predictor tests for BOTH: a
frame-drop burst converges back (no permanent overshoot); a normal release still holds-then-lands (no snap).

## Gates
- `run-checks.cmd` green + `godot-build.cmd` clean. **Do NOT run `stop-mmo`/gates that would kill a live session
  without flagging it** — if your shell can't gate cleanly, leave the work + review-request and the Orchestrator
  runs gates (coordinating timing with the user).

## Standing rules
One discrete revertable commit referencing this task; delete this file in that commit. **Safe Local Execution**.
You cannot run Godot — the human verifies the teleport/reconcile behavior live at 50ms.

## Acceptance
A lag spike / frame drop in UO mode no longer leaves the prediction permanently ahead — it converges back to the
server within a bounded time; the UO3 release behavior is preserved (no backward snap on a normal release). Gates
green.
