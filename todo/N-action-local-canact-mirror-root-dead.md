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

Acceptance: a jump triggered while dead / mid swing-root is declined locally (not sent), so it produces no
predicted-then-rejected correction; existing gate tests stay green.
