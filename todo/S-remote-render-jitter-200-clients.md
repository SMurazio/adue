# S — remote render jitter, measured (200-client stress) — two mechanisms, latency-free fix wanted

User report: "I see them jitter a bit" with 200 spawned clients. **Measured live (2026-07-02) via
client_render_trace on close-in wandering bots, 4s @60fps windows, A/B at 120 vs 200 bots:**

| metric | 120 bots | 200 bots |
|---|---|---|
| velocity reversals (>90° flips) | 27/241 frames (11%) | 62/241 (26%) |
| speedStdDev / meanSpeed | 2.08 / 4.34 (48%) | 3.44 / 4.84 (71%) |
| maxJerk (u/s per frame) | 9.1 | 30.4 |
| meanRenderVsAuth | 0.074u | 0.082u |
| server CPU (PID sampled 5s) | 68% of one core | ~107% of one core |
| client fps | 60 | 60 |

Render-path signature: smooth micro-steps → stall → small BACKWARD step → catch-up jump. The client is healthy;
the divergence-vs-auth is small (≤0.33u) — this is high-frequency sub-0.1u shimmer, not rubber-banding.

## Two mechanisms (separate fixes)

1. **Baseline shimmer (present at 120, ~11%):** the remote render EXTRAPOLATES-TO-NOW off the newest sample
   (`newest.Position + newest.Velocity × elapsed`, zero added latency by design). EVERY arriving snapshot
   corrects the extrapolated position slightly — sources: Q12.4 position quantization (±1/32u per sample),
   heading changes mid-flight (bots waypoint-steer; same mechanism as [[N-gnoll-walk-jitter-extrapolation]]),
   and arrival-time noise. The correction is absorbed INSTANTLY → a >90° velocity flip that frame.
2. **Load amplification (200): tick-cadence jitter.** The single-threaded tick loop nears one-core saturation
   (68%→107% from 120→200 bots — superlinear). Late/bunched ticks ⇒ bursty snapshot arrivals ⇒ extrapolation
   overshoots further and corrects harder (reversals 2.3×, jerk 3.3×). NOT a capacity wall (200/200 ran, no
   errors, client 60fps) — but past the smoothness budget.

Also observed: at 200, entities churn in/out of the interest radius noticeably more (traces died twice when the
target left AOI) — spawn/despawn popping is a separate visible artifact at that density.

## Fix directions (user's no-added-lag rule: prefer latency-free, cosmetic)

- **Smooth the correction:** absorb the per-sample position error over a few frames (a decaying render-offset,
  exactly like the local predictor's reconcile smoothing) instead of instantly — latency-free, kills the >90°
  flips without delaying fresh data. PRIMARY candidate.
- **Ease velocity-direction changes in the extrapolation** (blend heading) — helps the turning-entity component
  (also the gnoll fix candidate).
- (Rejected class: a bigger interp buffer — adds latency; keep as a diagnostic knob only.)
- Server side, longer-term: the 200-client tick saturation is its own scaling item — profile what's superlinear
  (AOI gather? snapshot build? send syscalls?) BEFORE optimizing (measure first). At 120 the server has headroom.

## Acceptance

- Headless first: the smoothness regression harness (see [[N-remote-smoothness-tooling]]) reproduces the
  reversal shimmer from synthetic bursty/quantized sample streams, and the fix drives constant-velocity
  reversals to 0 and bursty-arrival reversals near 0 WITHOUT adding render latency.
- Live re-measure with client_render_trace at 120 and 200: reversals ≪ 11%/26%; feel-confirm by the user.
- Netcode/presentation → full rigor: measure, independent review, feel-test.
