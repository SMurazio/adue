# S17 — Godot M2: local-player prediction + reconciliation (the feel payoff)

Severity: should-fix (this is what actually makes the local avatar responsive + smooth — the whole
reason for moving to Godot). Design: `docs/godot-client-design.md` (M2 + the prediction section).

## Prerequisites (do these first — they're server/shared changes, partly NOT in the client)

1. **S15** + **S16** (the Godot client exists and renders confirmed state).
2. **Prereq A — shared movement rule:** extract the tile-step rule into a pure function in
   `Mmo.Shared`, used by BOTH `WorldEntity.TryStep` (server) and the client predictor. Single source
   of truth → no prediction drift. *(Server/shared change — implementer-actionable, headless.)*
3. **Prereq B — input-sequence echo:** server tells each client the last `MoveStep` sequence it
   processed (field on `WorldSnapshot`), so the client can drop acked inputs and reconcile.
   Small protocol addition (version bump). *(Server change — implementer-actionable, headless.)*
   Fallback if deferred: snap-to-authoritative-on-divergence (works for tile-stepped, since
   mispredicts are rare) — but the input-seq echo is the correct version.

## Goal

Local player applies each `MoveStep` immediately (predict, via the shared rule), buffers
unacknowledged inputs, and reconciles against authoritative snapshots (replay unacked inputs from the
confirmed tile). Result: instant, smooth local movement that stays server-authoritative. Remotes
unchanged (still interpolated). Tile-stepped determinism makes mispredicts rare and corrections
cheap.

## Fences

No lag compensation, no rollback, no extrapolation. Prediction is local-player only; remotes stay
interpolated.

## Acceptance

- Local avatar responds instantly to input with no rubber-band, while remaining authoritative.
- A forced mispredict (e.g. stepping into a wall the client didn't expect) corrects cleanly.
- Prereqs A/B covered by tests; `run-checks.cmd` green; Godot client builds (`godot-build.cmd`).

## Blocked

S16 is now complete and manually verified, so the original prerequisite blocker is gone. This task
is still blocked on an Orchestrator architecture decision because it conflicts with current
authoritative planning text:

- `docs/feature-roadmap.md` says to keep confirmed tile tweening and not add prediction unless
  movement latency is measured as unacceptable.
- `docs/networking-design-plan.md` section 2 says no client prediction now, probably not ever, and
  only to revisit local-player prediction as a measured exception.
- `docs/godot-client-design.md` and this todo describe prediction/reconciliation as M2.

The human just verified the Godot movement direction/feel as correct after S16 fixes, so the measured
trigger for prediction has not been established in this implementation pass. Because prediction
requires protocol and architecture decisions (shared movement rule, snapshot/input-sequence echo,
version bump, reconciliation behavior), the Implementer should not start it until the Orchestrator
explicitly resolves the plan conflict and confirms S17 should supersede the no-prediction roadmap.
