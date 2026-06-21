# NET5 — ack-driven re-send of unacked commits (tail recovery) + bounded ForceResync fallback

PRODUCTION on `main`. **PRIORITY S — the tier-1 recovery fix.** Implements tiers 1–2 of
`docs/movement-loss-degradation-tiers.md`. Diagnosed live via DIAG1 + reasoning (2026-06-21).

## Diagnosis (confirmed via DIAG1)
At 100ms + ≤3% loss the local prediction strands AHEAD of the server and never recovers. DIAG1 readings: `lead`
scales with loss (1 / 2 / 6 at 1 / 3 / 10%), `rec` is almost all **Matched** (the prediction is CORRECT, just
unconfirmed), `Snapped=0`. The strand is **link 1 (delivery), specifically the TAIL**: the redundant re-send
(already 8-deep — `MoveInputRingCapacity`/`StepCommitRingCapacity=8`) rides SUBSEQUENT packets, so a mid-stream
single loss recovers within ~1 packet. But the **last commit of a burst has no following packet** to carry its
redundant copy; if it drops (~loss%) it is never re-delivered → server stuck one (or N) behind → permanent
desync, worst on stop. `lead=2` while moving is the normal RTT in-flight (healthy); the strand is the +1 that
never drains.

## Scope / target
**Seamless recovery at ≤3% (tier 1)** — realistic bad links (1% = normal-bad, 3% = genuinely poor). 5%+ is
**failure-state** (degrade via the resync fallback / NET4), NOT a seamless target. **Do NOT bend the
architecture for 10%.**

## Fix
1. **Ack-driven re-send (tier 1, seamless).** The client already knows the unacked set: `lead = PredictedStepSeq
   − RecipientStepSeq (conf)`. While `lead > 0`, keep shipping the current StepCommit ring (the existing 8-deep
   redundant window via `SendStepCommitBatch`) at ~cadence, **including after movement stops**, until `conf`
   catches `pred`. The server dedups what it has (`TryConsumeCommitSequence`) and applies the recovered one at
   its authored tick → `conf` catches up → `lead` drains → **NO snap** (the prediction was right, just confirmed
   late). Bound the re-send rate (~1 batch/cadence); stop when `lead==0`.
2. **Bounded ForceResync fallback (tier 2).** If the re-send has fired ~K times / ~T ms and `conf` has NOT
   advanced (the commit is genuinely undeliverable — heavy loss or rejected), call the RESYNC1 primitive
   (`ForceResync`) to converge. "Re-sent K times, ack still stuck" is a clean, false-trip-proof trigger (we
   actively tried and failed) — NOT a raw `lead` watcher (that is the reverted UO5 stall-counter trap). Tune
   K/T so normal ≤3% recovery NEVER reaches it (it heals via re-send first).
3. **Fix the `recv/s` metric** (DIAG1): it misreads 1.0 at both 1% and 10% — confounded by idle, or
   under-counted. Make `recv/s` reflect the true snapshot arrival rate so the next measurement is trustworthy.
   Likely a separate small commit.

## Do NOT (this scope)
- Do NOT change the cooldown / future-cap rejection **unless** verification shows it firing in the REALISTIC
  range (see Verify). 10%-driven changes are out of scope.
- Local-player only; no remote-entity changes. No new protocol version if avoidable (re-send reuses the
  existing `StepCommitBatch`).

## Mandate (disciplined — headless repro FIRST)
1. **Reproduce headless** in `tests/Mmo.Client.Core.Tests/TimingFaithfulReconcileHarnessTests.cs` (or the right
   harness): model the commit delivery, drop the TAIL commit, STOP input, and assert that on the CURRENT code
   the server's accepted seq (`conf`) stays permanently behind (`lead` stuck). **If you cannot reproduce it →
   STOP and report** (the mechanism differs from the hypothesis).
2. **Then implement** the re-send → assert `lead` drains to 0 (seamless, no forced snap) at a ≤3%-equivalent
   drop.
3. **Bounded fallback** → at a heavier drop where re-send can't land, assert `ForceResync` fires and converges.
4. **Regression guard:** steady-walk + clean low-loss (≤3%) MUST stay green — NO spurious re-sends in clean
   play (re-send only while `lead>0`), NO forced snaps at ≤3% (heals via re-send first).

## Verify (orchestrator)
- Standard gates + a 120/30s stress.
- **Read DIAG1's `rejectTooEarly` at 3% (NOT 10%):** if the future-cap is rejecting recovery backlogs in the
  realistic range, file a follow-up to relax it to pace + generous-bound. If it only fires at 10%, leave it.
- Human live: 100ms + 1% and 3% → the tail strand now heals (lead drains, no snap); 5–10% → degrades to resync
  (acceptable).

## Standing rules
One discrete revertable commit per change (the `recv/s` fix is likely separate from the re-send+fallback);
reference this task; delete this file on success. Safe Local Execution; you cannot run Godot/gates (the
orchestrator does). Emit `review/review-request-net5.md`.

## Acceptance
At 100ms + ≤3% loss a lost tail commit is re-delivered and the prediction recovers with **NO visible snap**
(`lead` drains to 0). Genuinely-undeliverable cases (heavier loss) fall back to `ForceResync` within a bound.
Steady-walk + clean low-loss stay green. `recv/s` reads the true snapshot rate. Gates green.
