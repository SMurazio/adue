# N — remote-smoothness tooling: make the crowd-jitter class measurable in CI and live (user-requested)

User (2026-07-02, during the 200-client jitter session): "would it help you to build more specific tests to
test this type of things? you can come up with any improved tooling to catch issues like this one." Yes — the
stress gate measures SERVER health (tick ms, errors, bandwidth) and misses REMOTE RENDER smoothness entirely;
client_render_trace exists but is single-entity, dies when the target leaves AOI, and needs a live session.
Four pieces, roughly in leverage order:

## 1. Headless remote-render smoothness regression harness (CI-able) — HIGHEST VALUE
Drive `RemotePositionInterpolator` (the real class) with SYNTHETIC sample streams and assert smoothness bounds
frame-by-frame (60fps sampling), mirroring client_render_trace's metrics (speedStdDev / maxJerk / reversals):
- constant velocity, regular 50ms arrivals → reversals == 0, speedStdDev ~0 (the clean baseline);
- Q12.4-quantized positions (round each sample like PositionEncoding) → bound the added noise;
- TURNING entity (heading rotates like a waypoint bot / chasing gnoll) → quantify the perpendicular swing
  (this is the [[N-gnoll-walk-jitter-extrapolation]] measure, done headlessly);
- BURSTY arrivals (late tick then two samples back-to-back — the 200-client server signature) → quantify the
  overshoot-correct spike; this is the repro for [[S-remote-render-jitter-200-clients]] mechanism 2.
This turns "the crowd shimmers" into failing numbers BEFORE a fix and green bounds after — and pins against
regression forever.

## 2. Snapshot-arrival cadence telemetry (client) — the missing discriminator
Track inter-snapshot arrival deltas (mean/p95/max over a rolling window) + entities/snapshot in MmoClient
(NoteSnapshotReceived already exists) and expose via client_telemetry + the F3 HUD. Distinguishes "server sends
bursty" (mechanism 2) from "render model wobbles" (mechanism 1) in ONE read, live. No protocol change.

## 3. Crowd-smoothness aggregate (control channel)
client_render_trace, but over ALL visible remote moving players for N seconds: per-entity reversals/jerk
aggregated to one "crowd smoothness score" + worst offender. Survives AOI churn (an entity leaving mid-window
just ends its own sub-trace). Makes the live A/B a single call instead of picking bots by hand.

## 4. Server tick-cadence jitter metric
/metrics + the stress report show avg/max tick COST but not SCHEDULE jitter — a tick can be cheap yet late.
Record inter-tick wall-clock deltas (p95/p99/max drift from the nominal 50ms) in ServerMetrics; surface in
/metrics and the review-stress summary. This is the server-side number that should correlate with mechanism 2
(68%→107% single-core load at 120→200 bots).

Diagnostics guardrail: all live pieces are runtime toggles/reads (control channel / F3), no launch flags.
NOTE: implementation needs a build → do NOT start while the user's live server/client session is up (DLL lock;
see dont-gate-while-user-playing). Serves [[S-remote-render-jitter-200-clients]] and the gnoll-jitter measure.
