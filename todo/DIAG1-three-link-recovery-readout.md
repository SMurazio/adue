# DIAG1 — instrument the 3 recovery links live, to find WHERE loss strands the prediction

PRODUCTION on `main`. **PRIORITY S — gates the tier-1 recovery fix.** After 3 misses (UO5-stall, NET2,
NET3-live) we MEASURE before the 4th fix. See `docs/movement-loss-degradation-tiers.md` ("recovery chain has
3 links").

## Why
At 100ms + ~3% loss the local prediction strands ahead of the confirm and **never recovers**. A permanent
desync means ONE of three links is broken; we need to SEE which before fixing:
1. **Delivery** — does the server RECEIVE the (redundantly re-sent) commit at all?
2. **Application** — does the server APPLY/ack it, or REJECT it (cooldown / NET3 future-cap, e.g.
   `commit_too_early`)?
3. **Learning + re-base** — does the client RECEIVE the updated ack and does the lead DRAIN, or does UO3 hold?

## What to build (live diagnostic — no restart; Diagnostics-are-live-toggles guardrail)
A live readout, for the **local player**, surfaced where the human can read it during a loss burst — an **F6
panel readout** (preferred) and/or a CSV trace toggle. Show, updating live:
- **`pred`** — client predicted step-seq (`PredictedStepSeq`).
- **`conf`** — client's last received `RecipientStepSeq` (what the client has LEARNED the server accepted).
- **`lead`** — `pred - conf` (the in-flight that must drain to recover).
- **`recv/s`** — snapshots received per second (is the confirm channel alive?).
- **reconcile outcome counters** — Matched / Corrected / Snapped since reset.

Server side (to separate "server didn't accept" from "client didn't learn"), via the existing
`ServerMovementTrace`/server log or a small added counter — for the local player's entity:
- **`srvSeq`** — the server's ACTUAL accepted `StepSequence`.
- **`recvCommits`** — commits received (incl. redundant) and **`rejects`** by reason (`commit_too_early`, future-cap, etc.).

## How we'll read it (the discriminator)
- `srvSeq` advances to match `pred` but `conf` lags `srvSeq` → **link 3** (server→client confirm loss / no re-base).
- `srvSeq` stalls while `recvCommits` climbs + `rejects` climb → **link 2** (server REJECTING delivered commits).
- `srvSeq` stalls and `recvCommits` does NOT climb → **link 1** (redundant delivery not recovering the commit).
- `srvSeq`==`pred`, `conf`==`srvSeq`, but `lead`>0 and the avatar stays stranded → **link 3b** (reconcile/UO3 not re-basing the render).

## Scope
Local-player only; no protocol/behavior change to movement — this is **measurement only** (read-outs +
counters). Do not attempt a fix in this task; the fix is a follow-up speced from what DIAG1 shows.

## Gates
`run-checks.cmd` green + `godot-build.cmd` clean. Safe Local Execution; do NOT kill a live session without
flagging. You cannot run Godot — the human reads the live readout under 100ms + 3% loss and reports the four
numbers + which link is stuck.

## Standing rules
One discrete revertable commit referencing this task; delete this file in that commit on success.
Emit `review/review-request-diag1.md` when done, noting how to toggle the readout and what each field means.

## Acceptance
Under 100ms + 3% loss the human can read `pred / conf / lead / srvSeq / recvCommits / rejects` live and we can
state unambiguously which of the 3 links is stuck. Measurement only — no movement behavior change. Gates green.
