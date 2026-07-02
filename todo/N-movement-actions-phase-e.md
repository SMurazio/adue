# N — movement-actions Phase E: skill-input wiring + animations (needs ART + human feel-test)

Per `docs/movement-actions-design.md` §6 Phase E. Depends on Phase D (`S-movement-actions-phase-d`).

## Scope

- Bind player skill inputs (hotkeys / skill bar) to action triggers (jump/charge/roll).
- Client animations: the jump animation driven by the real replicated `VerticalOffset` (not a faked arc);
  charge/roll animations on the Cato model's state machine.
- Cosmetic polish: landing dust/squash, roll dust, optional early `ActionRejected` cancel.

## Gate

- Live human feel-test under latency: i-frame fairness, jump responsiveness, remote player's jump height
  reads correctly on another screen. **Agent can implement + headless-gate; the feel verdict is the
  user's.**

Note: the Cato model currently has idle/walk/run/attack states (+ kick-on-attack). Jump/charge/roll
animations may need ART before this phase completes — coordinate with the user's ongoing Cato work.

Builds on [[movement-actions-framework]].
