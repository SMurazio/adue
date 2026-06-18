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

S17 depends on S16: the Godot client must exist, render confirmed state, and be manually verified
before local-player prediction is added. S16 is currently blocked because this environment has no
Godot .NET executable configured for `godot-run.cmd`, and the remaining acceptance requires human
editor/runtime verification with two clients. The server/shared prerequisites A/B are deliberately
not started yet, to avoid landing prediction protocol changes before the Godot confirmed-state
client is verified.
