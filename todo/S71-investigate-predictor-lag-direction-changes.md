# S71 — Investigate + tighten LocalPlayerPredictor lag on direction changes (keep divergence sub-tile)

Severity: movement polish (prediction). **Investigation-led.** The S69 motion instrumentation found the one
real residual movement nit: during **rapid direction changes**, the predicted local render position **lags
the server-confirmed tile by ~2–3 tiles**, then **catches up with a ~1.3-tile single-frame jump** (a visible
reconcile snap). Concretely from a 10 s zigzag capture: one event reached **2.9-tile divergence** with the
render trailing the server, then `frameDelta` 1.34 in one frame catching up; only **5 of 593 frames** (0.8%)
diverged past 1.5 tiles; **straight/diagonal/idle are clean** (divergence <1 tile, `snapCount` 0). So normal
movement is smooth; only aggressive reversals trigger it.

**Goal (UO-faithful — verified against ClassicUO):** UO does NOT smooth/lerp corrections — it hard-snaps
(`DenyWalk` → `ClearSteps` zeroes the tween offset + `SetInWorldTile` hard-sets) and stays smooth by keeping
divergence *tiny* via per-move ack so it rarely needs to snap. So **do NOT add a smooth-reconciliation layer.**
Instead, find why our predictor falls ~3 tiles behind on a direction change and **keep divergence sub-tile**,
hard-snapping the rare residual like UO.

## Investigate (diagnose first, in the review-request)
- Read `src/Mmo.Client.Core/LocalPlayerPredictor.cs` end to end: the present-time step tween, the
  direction-change/turn handling (S59 turn-then-move + S63 turn-delay), and the reconcile-vs-server path
  (when/how it snaps). Also how `MmoClient` feeds it intent + confirmed snapshots.
- Form a concrete root-cause for the lag. Leading hypothesis: the **turn-delay (S63) stalls predicted
  stepping** while the server keeps stepping (so the render falls behind through a direction change), and/or
  the reconcile only corrects on a large divergence so the render is allowed to trail several tiles. Confirm
  or refute against the code; identify the exact mechanism.

## Fix (if the root cause + fix are clear, client-only, and parity-safe — else STOP and report the diagnosis + options)
- Tighten so the predicted render tracks the server within **< ~1 tile** through direction changes (keep
  divergence sub-tile), and hard-snap any rare residual (UO-style) rather than letting it drift then jump.
  Exact approach depends on the diagnosis (e.g. don't stall predicted stepping on a turn; step the predictor
  in lockstep with the server cadence across direction changes; tighten the reconcile tolerance).
- **MUST preserve server↔predictor parity** — the existing parity test that drives the real `WorldEntity`
  against the predictor must stay green (a mismatch reintroduces the S56 rapid-direction snap). Client-only;
  no server/protocol change.

## Constraints
- Diagnosis-led: the review-request must explain the root cause + why the chosen fix keeps divergence small
  without breaking parity. If the fix needs a server/protocol change or has a real tradeoff, do NOT implement
  — report the diagnosis + options for the Orchestrator to decide.
- Run `.\.shared\skills\mmo-dev\scripts\run-checks.cmd` before/after (try it; if Bash denied, note + continue
  — Orchestrator runs the gates). You can't run Godot — Orchestrator runs `godot-build` + **re-measures via
  the S69 instrumentation** (drive a zigzag, confirm maxDivergence stays small + the catch-up jumps are gone)
  after a relaunch. **Safe Local Execution** binds you.

## Acceptance
- `run-checks` green incl. the predictor↔server parity test. A diagnosis of the lag root cause + a tightening
  fix that keeps direction-change divergence sub-tile (or a clear surfaced fork if not safe to implement).
  Review-request → `review/review-request-s71-predictor-lag.md`. Do NOT commit or delete the task file.
