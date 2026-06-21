# Movement under packet loss — graceful degradation tiers (policy)

Decision record (Orchestrator + user, 2026-06-21). Canonical target behavior for local-player movement under
packet loss in UO mode. RESYNC1 / DIAG1 / tier-1 fix / UO5 / NET4 implement against this.

## The core problem we keep missing
Lag (pure latency) recovers fine; **packet loss "never recovers"** — the prediction strands ahead of the
confirm, permanently (user, repro 100ms + 3–10% loss via clumsy). Three fixes (UO5-stall, NET2, NET3) have NOT
fixed it. Snapping the prediction back (a bounded client-side hold) only **masks** this as rubberband; it does
not make a lost step actually get confirmed. **Tier-1 smooth recovery at 3% requires REAL recovery — the lost
step gets confirmed — not a client-side snap.**

## The recovery chain has exactly 3 links; a permanent desync = one is broken
1. **Delivery** (client→server): a lost commit must be re-delivered (redundant window, NET2). *Measure:* does
   the server actually RECEIVE it?
2. **Application** (server): the server must APPLY + ack the delivered commit. Our cooldown gate / NET3
   future-cap can REJECT it (anti-speedhack) → never acked. *Measure:* does the server's accepted step-seq
   advance? does a reject-count climb (e.g. `commit_too_early`)?
3. **Learning + re-base** (server→client + client): the client must RECEIVE the updated ack (snapshot /
   `RecipientStepSeq`) AND re-base its prediction on it. UO3's uncapped hold can keep the prediction ahead
   instead of re-basing. *Measure:* does the client's received-confirm-seq advance? does the lead drain?

Robust netcode (Gambetta / Quake / Overwatch — our references) keeps all three unbreakable: the server
processes and acks **every delivered input** (it *paces*, it never rejects mid-stream), and the client
**re-bases on every authoritative snapshot** and replays only still-unacked inputs. Our cooldown-**REJECT**
gate (link 2) and UO3 **hold** (link 3) are the architectural splinters that let a desync become permanent.

## MEASURE before the 4th fix (DIAG1)
Per the new review rule (no self-certified guesses), **DIAG1 instruments all three links live at 3% loss**
(predicted seq / received-confirm seq / server-accepted seq + reject-count + lead + reconcile outcomes) so we
SEE the stuck link, then fix exactly that. **Leading hypothesis (to be confirmed by DIAG1):** link 2 — the
server **PACES** delivered commits instead of rejecting them (cooldown becomes a *pacer*; anti-speedhack
becomes a generous *queue-depth bound*, not a per-commit early-reject) **+** client re-bases each snapshot →
every delivered commit eventually confirms → tier-1 recovers smoothly with no snap.

## Tiers (target behavior)

| Tier | Loss (target) | Trigger (observed) | Behavior |
|---|---|---|---|
| **1 — Recover** | 0–3% | normal | **real recovery**: delivered commits all confirm + client re-bases → converges with **no visible snap** |
| **2 — Rubberband** | 4–6% | lead exceeds `bound` (commits truly lost beyond recovery) | **forced resync** toward server (visible rubberband), stays connected, recovers |
| **3 — Reconnect** | 6%+ | no confirm for a sustained window (N × cadence) | hard resync → **reconnect + resync loop until healthy** |

`bound = ceil(RTT / cadence) + margin` tiles (≈3–4 at 100ms / ~150ms cadence).

## Decisions
- **No deliberate crash.** Tier 3 hard-resyncs + reconnect/resync loops with a "reconnecting…" state; real
  disconnect is only the last-resort fallback after N failed attempts.
- **One shared resync primitive** (RESYNC1: `ForceResync()` — snap to server tile, clear in-flight, re-anchor
  seq, snap render). Built first as a manual F6 button + Alt+R; reused by tier 2 and tier 3.
- **The bounded hold is tier 2 *masking*, NOT tier 1.** Tier-1 recovery is the real fix (links 2+3), informed
  by DIAG1.

## Work order
1. **RESYNC1** — manual Force Resync + the shared primitive (foundation, immediate escape hatch).
2. **DIAG1** — instrument the 3 links at 3% loss → identify the stuck link. **Gates the tier-1 fix.**
3. **Tier-1 fix** (likely server pace-not-reject + client re-base) — speced AFTER DIAG1 says which link.
4. **UO5** — bounded hold = the tier-2 forced-resync safety for 4–6% only (not tier 1).
5. **NET4** — tier-3 watchdog + reconnect/resync loop.

## Appendix — external threshold suggestion (another agent, 2026-06-21; for LATER reconciliation)
Not adopted yet — captured so it isn't lost. Reconcile against our tiers when we revisit thresholds.

- **Target (0%)** — baseline optimization goal (server architecture + serialization).
- **Acceptable (<1%)** — completely seamless via prediction + interpolation.
- **Degraded (1–3%)** — minor visual artifacts (slight jitter) acceptable; must stay playable + fair.
- **Failure (>5%)** — show **network-instability UI indicators**, **aggressively roll back crucial actions**,
  prepare to **disconnect** if loss persists.

Deltas vs our tiers above (worth folding in later):
- **Network-instability UI indicator** — we don't have one; good addition to tier 3 / failure state.
- **Action-rollback framing** — "roll back crucial actions" is our forced-resync (`ForceResync`) primitive,
  framed around important actions rather than just position.
- **Tighter thresholds** — "seamless" target is <1% here (vs our 0–3% tier 1); a 3–5% middle band is left
  implicit (our tier-2 rubberband).
- **No conflict with "must recover at 3%":** "slight jitter acceptable at 1–3%" means transient corrections
  are fine, NOT that a permanent desync is fine. The DIAG1 → tier-1-recovery work (kill the permanent strand)
  still stands; this only says we don't need pixel-perfect smoothness at 1–3%, just playable + recovering.
