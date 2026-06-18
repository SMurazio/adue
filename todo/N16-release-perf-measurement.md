# N16 — Measure performance in Release, not Debug

Severity: nice-to-have (methodology fix). Surfaced during S22 review.

## Why

All S20/S21/S22 stress numbers were captured against **Debug** builds, because
`review-stress.cmd` / `start-server.cmd` run the Debug output. Debug perf is not representative
(no optimization, different JIT/codegen). Concretely: S22's post-fix Debug stress showed
`tickMs max ≈ 33 ms`, but the equivalent **Release** run showed a single ~22 ms outlier the whole
minute (and that one is OS scheduling, gc=0). We were chasing inflated numbers.

## What

- Add a Release path to the perf tooling: either a `--release` flag or a dedicated
  `review-stress-release.cmd` that builds and runs `-c Release` server + stress output.
- Keep the Debug path for fast iteration / functional checks; use **Release** for any perf
  acceptance numbers going forward.
- Document in `docs/runbook.md` that perf/stress acceptance numbers must come from a Release build.

## Acceptance

- A documented one-command way to run a Release 120-client/60s stress and read `tickMs`/`gc`/budget.
- Runbook states the Debug-vs-Release rule for perf measurement.
- `run-checks.cmd` green (no behavior change to the Debug path).

See `.shared/memory/server-tick-performance.md` for the full context.
