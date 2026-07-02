# N — client can-act mirror: also decline a locally-predicted action while rooted / dead (minor)

From the B2 independent review (commit `ae07527` + the cooldown-mirror follow-up). LOW priority.

**Context.** B2's client predictor declines a re-trigger on one-at-a-time AND on the mirrored per-action
cooldown (`ContinuousPredictor.BeginAction`), so the common double-press never mispredicts. The server's
full can-act gate (`ServerActionExecutor.CanStart` + `GameServer.HandleActionIntent`) ALSO rejects a
trigger while the entity is **movement-rooted** (mid swing-root) or the session is **dead**. The client
does NOT mirror those two, so triggering a jump during the attack-root window (attack → immediately jump)
or while downed predicts a jump the server rejects.

**Why it's only a nit.** Both are rare and ALREADY handled correctly — just not pre-declined: the rejected
action is absorbed by the standard reconcile as a bounded, converging correction (design §2.6, covered by
`Action_Rejected_...` in the gate). So it self-heals; it would only look like a brief predicted hop that
snaps back. The cooldown case (common, double-press) was the one worth pre-declining and is done.

**Fix (if the live feel-test flags it).** Mirror the two remaining gates client-side:
- Dead: the client knows its own downed state (LocalStats / HP); decline `SendAction` while dead.
- Movement-root: harder — the continuous client does NOT currently predict the swing-root (SendAttack uses
  AuthoredTick 0, no root mirror), so there is no local root window to check. Mirroring it would mean
  reconstructing the swing-root locally (a small attack-side change). Defer unless the feel-test shows an
  attack→jump rubberband.

**Phase-D escalation (from the Phase D independent review, F2).** The dash actions raise the stakes: the
attack→immediately-dash sequence (K/L during the swing-root window) is a NATURAL combat input, and the
mispredicted-then-rejected correction is now a **2.5–4.0u horizontal snap-back** (the whole dash length),
not a small hop. Still bounded + convergent (pinned by `Charge_RejectedByServer_ConvergesToServer`), but
much more visible. Also: the client cooldown mirror is a single conservative slot shared across all 3
actions (declines cross-action triggers, e.g. no jump for 2s after a charge, that the server's
per-(entity,action) clocks would accept) — a per-action mirror is the fix, and it's a predictor change.
Consider promoting this after the Phase D/E live feel-test.

Acceptance: a jump/charge/roll triggered while dead / mid swing-root is declined locally (not sent), so it
produces no predicted-then-rejected correction; cross-action triggers are declined only per the server's
per-action clocks; existing gate tests stay green.
