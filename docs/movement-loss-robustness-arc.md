# Movement loss-robustness arc (DIAG1 → NET6) — resolution + open levers

Decision/record, 2026-06-21. How the "movement desyncs under packet loss and never recovers" problem was
diagnosed and fixed. Pairs with `docs/movement-loss-degradation-tiers.md` (the policy).

## The problem
UO-mode movement (client-predicted, server-validated steps) desynced under packet loss: the predicted position
stranded ahead of the server's confirmed position and **never recovered**, getting worse the longer it ran and
faster with more loss (a runaway). Latency alone recovered fine; only *loss* broke it. Three earlier attempts
(UO5-stall, NET2, NET3) had each "passed" an author-written headless test yet failed live.

## The arc
1. **DIAG1** — instead of a 4th guess, instrument the recovery chain: a live F3 read-out of `pred` (predicted
   step-seq), `conf` (last server-acked step-seq), `lead = pred-conf`, `recv/s`, reconcile outcomes, plus
   server-side `srvSeq`/`recvCommits`/`rejects` in the trace. **Measure before fixing.**
2. **Live reading** — at 1% loss: `lead` stuck and scaling with loss (1/2/6 at 1/3/10%), reconciles almost all
   *Matched* (the prediction was correct, just un-acked), `recv/s` healthy. So: commits were reaching a correct
   predicted state but the server wasn't *crediting* them — a delivery/acceptance break, not a confirm break.
3. **NET5** — ack-driven re-send: while `lead>0` and the ack is overdue, re-ship the (already 8-deep) commit
   ring until `conf` catches `pred`, plus a bounded `ForceResync` fallback. Right mechanism — but live it didn't
   land: the re-send fired, `conf` never advanced, the fallback snapped after ~1.5s.
4. **The trace (independent diagnosis)** — overturned the orchestrator's future-cap hypothesis and found the
   real root (below). The harness had a blind spot (it never modeled an intent advancing the dedup cursor),
   which is exactly why NET5's tests passed while live failed.
5. **NET6** — the real fix (below). Plus a **renderer self-heal** for a loss-triggered *visual* bug.

## Root cause (NET6)
The client mints commit seqs and move-intent seqs off **one shared monotonic counter**, and the server
deduplicated **both** streams against **one cursor** (`ClientSession._lastMoveSeq`). So a keyup STOP intent (seq
N+1, sent 8× redundant) — or any interleaved direction-change intent — **burned the shared cursor past an
unconfirmed commit (seq N)**, and the server then dropped every re-send of N as "already seen"
(`ExtractFreshStepCommits` gates on `HeadSeq > lastSeq`) *before* it reached the (healthy) commit gate.
- Tail-on-stop: the keyup stranded the last commit → `lead` stuck at 1.
- Runaway while moving: each turn stranded a dropped mid-stream commit → `lead` climbed, never recovering.
**Fix:** a dedicated `_lastCommitSeq` cursor for commits, separate from the intent cursor. Commits now dedup
independently, so an intent can't strand a commit, and NET5's re-send (and the normal redundancy window)
finally land. No protocol/version bump.

## Loss-triggered visual bug (renderer self-heal)
A dropped **despawn** under loss left a stale entity visual in `_active`; the server's `NetworkIdPool` recycled
that id for a different entity, and `EntityRenderer.Sync` kept the stale visual (a departed player's cat shown
on a resource). **Fix:** `Sync` now rebuilds the visual when the entity at a recycled id needs a different
archetype than the parked one — self-healing within a frame.

## Result
Live: **solid at 10% packet loss** (failure-state territory) — no permanent desync, no runaway. The realistic
range (≤3%) recovers seamlessly.

## Decisions that mattered
- **Measure before the next fix.** DIAG1 turned "no idea why" into a precise reading in one run.
- **Author ≠ sole reviewer.** The independent trace overturned the orchestrator's wrong future-cap hypothesis
  and found the shared-cursor root; an independent test review caught that NET5's tests validated a re-impl,
  not the shipped path. This is now codified in `.shared/project.md`.
- **Pace-don't-reject** (relaxing the future-cap) was considered and is NOT needed at the current cadence (the
  trace showed the gate accepts late commits; the root was the cursor). It may return at faster speeds — see
  below.

## Open levers / caveats
- **Speed-untested (highest open risk).** Validated only at one cadence (~150ms / cooldown 140ms). At faster
  speeds the step-lead grows and the **future-cap** (`futureLead=4 ticks`) could start rejecting — the fix
  there is to make it (and NET5's K/T) **cadence-relative**. S106 (speed brackets) is the feature + the test
  vehicle for this.
- **NET5b** — headless test for the SHIPPED `DriveAckDrivenResend` (current tests validate a re-impl).
- **UO5** — re-verify whether a *frame-drop* burst still strands now that NET5/NET6 landed; deprecate or fix.
- **Reliable despawn** — would remove the high-loss visual blip (and ghosts) at no per-frame server cost;
  preferable to raising AOI radius (which taxes every snapshot).
- **NET4** — tier-3 watchdog + reconnect/resync for a genuinely dead link.
- **RENDER2** — recycled same-archetype id inherits prior interp state (Core, cosmetic).
- **Cosmetic masking** — latency-free render polish (error bleed-off, blend-small/snap-large, remote
  extrapolation) for smoother feel under loss/jitter.
