# S74 — Investigate: local prediction LAGS the true server (render falls behind, speeds up to catch up)

Severity: movement feel (prediction root). **Investigation-led.** Reframes the rubberband after S71/S72.

## Observation (from the human, with the S73 debug box)
Moving and changing direction (most apparent heading screen-down-left = world South, and on longer runs),
the rendered box **lags behind, then speeds up / jumps forward to catch up** — "it was trying to tween left,
I changed to down, and it has to speed up to get to the proper place." The facing arrow is correct. So the
render is **behind the TRUE server position** and racing forward to it — NOT running ahead. Telemetry showed
maxDivergence ~3.1 heading South.

## Second symptom — "EXTREMELY weird" trying to run INTO blocked tiles (sharpest repro)
The human reports that **running into a row of blocked tiles (a wall) produces very weird behavior** (the red
arrow in their screenshot was THEIR annotation marking the wall, not a render artifact — ignore it). This is
the same prediction system under stress: holding a direction into the wall, the predictor must **hold on the
blocked target** (`LocalPlayerPredictor.Tick` `_isWalkable` guard — it does not step into a non-walkable
tile) while the server does the same. Reproduce "hold a direction into the wall" (and "turn along the wall /
change direction while pressed into it") and characterize the weirdness — does the box jitter, creep past the
wall and snap back, oscillate, or mis-reconcile? Confirm the predictor's blocked-hold matches the server's
exactly, and that a held-into-wall intent + the snapshots it generates don't drive a reconcile fight. Treat
this as the **primary repro** — pressing into a wall removes the open-field timing noise and isolates the
predicted-vs-server divergence.

## Why S71/S72 missed it
S71 (no reconcile freeze) and S72 (treat recent-path confirms as benign) both assume the prediction is
**ahead** of the server (a benign lead to be left alone / not pulled back). But the human's observation is the
**opposite**: the prediction **under-runs** the server, so the render is behind and catches up. The divergence
metric (`render` vs `MmoClient.LocalTile` = the last-DELIVERED snapshot tile) can show a positive "lead" even
while the render lags the TRUE server, because `LocalTile` itself trails the true server by snapshot/interp
delay — so the metric masked this. **The investigation must reason about the TRUE server step timing, not the
divergence-vs-stale-confirmed number.**

## Investigate (diagnose first, in the review-request)
Read end to end and find WHY the predicted render falls behind the true server during movement + direction
changes:
- `src/Mmo.Client.Core/LocalPlayerPredictor.cs` — the present-time step tween + the cadence/turn-delay step
  schedule (`_nextStepAt`/`_nextEligibleAt`, the S59 turn branch, the S63 turn-delay). Does the predicted
  schedule advance at exactly the server's rate, or can it fall behind per turn / per step?
- `src/Mmo.Server/Runtime/WorldEntity.cs` (`TryStep`) + `GameServer` step loop — the authoritative cadence +
  turn-delay + WHEN a held-intent direction change is applied relative to the step tick. Compare the server's
  per-direction-change timing to the predictor's.
- `src/Mmo.Client.Core/MmoClient.cs` — how `LocalTile` (the "confirmed") is set from snapshots, and whether
  the local player is rendered from the PREDICTOR (present-time) or could be lagging behind it; the cadence /
  turn-delay values handed to the predictor (ServerHello / MovementSpeedChanged) vs the server's quantised
  values.
- Candidate roots to confirm/refute: (1) **turn-delay phase drift** — each direction change, the predictor's
  turn costs a different quantised time than the server's, so the predictor's step schedule slips behind over
  repeated turns; (2) **cadence phase drift** over a long run; (3) the predictor's present-time sampling
  lagging the server's actual present; (4) the predictor stepping conservatively (one step behind) on a turn.
- Determine whether the lag is **bounded + self-correcting** or **accumulates** over a run (the human says it's
  worse on longer down-left runs — points at accumulation).

## Outcome
This is diagnosis-led — produce the **root cause** with code references, then either propose a parity-safe
client-only fix (e.g. phase-lock the predicted schedule to the server's confirmed steps; fix a turn-delay /
cadence quantisation mismatch) **or**, if the lag can't be removed without it, make the case for **per-move
sequence/ack** (the UO approach we've avoided) as the real fix. Do NOT implement a speculative fix — if the
root + a safe fix are clear and small, implement it; otherwise STOP and surface the diagnosis + options for
the Orchestrator (this is a movement-model decision).

## Constraints
- Keep the predictor↔server parity test green (a fix must not break it). Prefer client-only; flag if a
  server/protocol change (e.g. per-move sequencing) is required — that's an Orchestrator decision.
- Run `.\.shared\skills\mmo-dev\scripts\run-checks.cmd` before/after (try it; if Bash denied, note + continue
  — Orchestrator runs gates + re-measures). You can't run Godot. **Safe Local Execution** binds you.

## Acceptance
- A diagnosis of WHY the prediction lags the true server (with code refs + whether it accumulates), and either
  a parity-safe fix (with run-checks green incl. parity + a test for the lag) OR a surfaced
  decision (per-move acking vs accept). Review-request → `review/review-request-s74-prediction-lag.md`. Do NOT
  commit or delete the task file.
