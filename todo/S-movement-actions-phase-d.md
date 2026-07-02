# S — movement-actions Phase D: charge + dodge-roll (new action definitions)

Per `docs/movement-actions-design.md` §6 Phase D. Phases A/B1/B2/C are SHIPPED + reviewed (the executor,
client prediction + reconcile of the ballistic jump, and the slime's real-hop reuse all work). Phase D is
the payoff test of the framework: **adding an action is a new def + trajectory + animation — no
executor/netcode change.**

## Scope

- **Charge** (player): `SlideStop` collision mode — a fast forward dash that early-stops deterministically
  on wall/entity contact (the gnoll's monster charge from P5 already exists via the shared executor — reuse
  the same def shape; this adds the PLAYER-triggered def + prediction).
- **Dodge-roll** (player): short dash + **server-authoritative i-frames** — damage resolution during the
  roll window is decided server-side only; a client cannot fake or extend i-frames.
- Both client-predicted through the existing action predictor path (B2), one-at-a-time enforced (§2.8),
  cooldowns validated server-side.

## Explicitly NOT in scope

- Skill-bar/hotkey UX polish + animations/dust (Phase E).
- New executor features. If either action needs an executor change, STOP and surface it — that
  falsifies the "actions are cheap now" claim and the orchestrator should know.

## Acceptance criteria

- Determinism tests per action (byte-identical client/server trajectory, incl. charge-into-wall early-stop
  under loss/latency, mirroring the Phase-B jump determinism suite).
- I-frame authority test: a damage event landing inside the roll window is negated SERVER-side; a client
  claiming i-frames outside the window takes damage.
- Rejected/spammed second trigger reconciles cleanly (one-at-a-time).
- Gate green; independent review (netcode-adjacent → full rigor).

Builds on [[movement-actions-framework]]. Trigger source for dev-testing can be the same dev/admin path
used in Phase B; player-facing binding is Phase E.
