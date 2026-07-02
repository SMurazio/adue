# N — profile the tick's superlinear cost at 150–200 clients (measure BEFORE any scaling work)

**DEFERRED by the user (2026-07-02): "don't invest on tradeoffs for now — just document them and document the
solution."** This file IS that documentation: the measurement below is the evidence, the levers section is the
tradeoff catalogue, and the profile is the agreed first step WHEN density work resumes. Two mitigations already
shipped meanwhile: the remote-render correction smoothing (`daa71fd`, the perception half) and the AOI radius
30 → 18 (`0d980dc`, ~2.8× less per-viewer AOI/snapshot load — likely pushes one-core saturation well past 200).

Measured (2026-07-02, live PID sampling during /stress, at the OLD radius 30): server CPU 68% of one core @120 bots → ~107% @200 —
superlinear, and the single-threaded tick loop nearing one-core saturation is what made snapshot cadence bursty
(the load half of the crowd-shimmer finding; the render half shipped in `daa71fd`). We do NOT yet know which pass
is superlinear. Per the project rule, measure first — this task is ONLY the profile + writeup, no optimization.

## How

- Instrument or sample the tick's phases at 120 vs 170 vs 200 bots: AOI gather, snapshot BUILD (per-viewer entity
  selection + encode), SEND (syscalls/packets), movement integration, monster AI, collision. The `/metrics` tick
  summaries + a per-phase stopwatch (behind a live toggle, per the diagnostics guardrail) or dotnet-trace on the
  server process.
- Deliverable: a short docs/ note ranking the phases' growth (linear vs superlinear in clients × visible-density),
  with the numbers. THEN decide the lever (see below) as a follow-up decision with the user.

## Candidate levers (context for the decision, NOT scope)

How comparable games run hundreds+ (user asked, 2026-07-02):
- **WoW-class**: movement replicated as EVENTS (start/heading-change/stop + client-side simulation; NPC splines) —
  ~zero per-tick cost per steady mover; hides jitter by accepting remote latency (conflicts with our
  no-added-lag principle up close, fine at distance).
- **Planetside-class**: SEND-RATE LOD — full rate near the viewer, lower Hz tiers further out + dead-reckoning.
  Our correction smoothing + velocity dead-reckoning already tolerate sparse samples (the harness's bursty
  scenario is exactly this), so LOD is the most compatible lever.
- **Skip-unchanged-velocity re-sends**: a half-step toward event-driven for steady movers (velocity already
  replicated); careful — reintroduces the staleness class the per-tick force-include retired.
- **Threading/split**: the guardrail says single process until metrics justify — this profile IS those metrics.
- **Albion-class (the closest comparable — also a C# server)**: one process per ZONE scaled horizontally, hot
  zones live-migrated to dedicated hardware, zone population CAPPED + queued (the "smart cluster queue" exists
  because uncapped ZvZ lag was chronic), remotes rendered with ~100-200ms interpolation delay at ~10Hz-class
  update rates, plus years of hot-loop optimization. Existence proof that a C# single-process zone hosts ~300 in
  combat — but note every piece trades something (remote latency, zone caps) we currently don't. Our unprofiled
  tick at ~107%/core with 200 all-moving bots is roughly their starting point, not their ceiling.

Design target remains 120–150 visible (holds with headroom); 200 all-moving bots in one small map is a worst
case. Relates to [[N-remote-smoothness-tooling]] (#4 tick-schedule jitter metric would ride along nicely).
