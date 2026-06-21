# NET4 — tier-3 heavy-loss watchdog + reconnect/resync-until-healthy (6%+ loss)

PRODUCTION on `main`. **PRIORITY N — do AFTER RESYNC1 + UO5.** Bigger than the others (a connection state
machine). Implements **tier 3** of `docs/movement-loss-degradation-tiers.md`.

## Goal
When packet loss is heavy enough that confirmation effectively stops (link near-dead, target ~6%+), the client
must stop trying to predict through it, **hard-resync to server truth, and keep reconnecting/resyncing until
healthy** — with a visible "reconnecting…" state. **Never deliberately crash** (per the policy); an actual
disconnect is only the last-resort fallback after N failed reconnect attempts.

## Mechanism (investigate + design; raise forks rather than guessing)
1. **Confirm-stall watchdog (observed, not a loss-%):** track time since the last `RecipientStepSeq` advance /
   last snapshot. If no confirm for a sustained window (~N × cadence, e.g. ≈1s — tune), the link is treated as
   failing → enter the tier-3 path.
2. **Hard resync:** call the RESYNC1 primitive (`ForceResync()`) to snap to server truth and stop the runaway
   prediction.
3. **Reconnect/resync loop:** if confirms resume shortly after the hard resync, recover silently. If they do
   NOT (sustained), drive a connection state machine: surface a visible **"reconnecting…"** UI, re-establish
   the session (disconnect → reconnect → re-handshake → full state resync), and retry until healthy. Back off
   between attempts; give up (real disconnect / error UI) only after N attempts.

## Open questions to resolve in design (surface, don't decide unilaterally)
- Does a reconnect re-use the existing login/session handshake, or do we need a lighter "resume session"?
- What's the authoritative full-state resync on reconnect (re-request a full snapshot baseline)?
- Watchdog thresholds (stall window, attempt count, backoff) — propose values, validate at 6–10% loss.

## Verification
- TEST1 / harness invariant: at heavy drop (e.g. 8–10%+ with a sustained gap) the watchdog trips, ForceResync
  fires, and the client re-achieves sync (no permanent desync, no crash).
- Live (human): 6%+ via clumsy → "reconnecting…" shows, session recovers and resyncs; lower loss (tiers 1–2)
  does NOT trip the watchdog (no false reconnects during normal rubberbanding).
- Gates: `run-checks.cmd` + `godot-build.cmd` green; standard stress gate (120/30s) unaffected.

## Standing rules
One discrete revertable commit referencing this task; delete this file in that commit on success. Safe Local
Execution; do NOT kill a live session without flagging. You cannot run Godot — the human verifies live.
Emit `review/review-request-net4.md` when done.

## Acceptance
At ~6%+ loss the client hard-resyncs and enters a visible reconnect/resync loop that recovers to a synced state
(never a deliberate crash; real disconnect only after N failed attempts). Tiers 1–2 (≤6%) do not trip the
watchdog. Gates green.
