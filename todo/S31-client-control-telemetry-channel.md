# S31 (T2) — Client debug control + telemetry channel (localhost) + autopilot

Severity: should-fix. Depends on S30 (T1). See `docs/client-control-telemetry-design.md`.

## What

Add a debug control channel to the Godot client, **off by default**, enabled by a flag
(`MMO_DEBUG_CONTROL_PORT`, unset → no listener). When enabled, open a **localhost-only** TCP listener
(`127.0.0.1` — never `0.0.0.0`) speaking line-delimited JSON request/response:

- **Commands:** `move {dir, durationMs}` / `stop`, `chat {text}`, `toggle_perf`, `toggle_fullscreen`,
  `autopilot {pattern, durationMs}` (scripted movement loop for repeatable repro).
- **Queries:** `telemetry` (fps, frame ms last/max/avg, per-section `_Process` timing from S30, gc
  gen0/1/2, hitch count), `interp` (q/cadence/confirmed tile/latency), `entities` (id/tile/render pos),
  `state` (connection/login/role/zone).
- **Telemetry CSV sink:** during `autopilot`, append per-frame rows (frame ms + per-section timing +
  gc deltas) to `.run/client-frames.csv` so a driver can pull a full trace.

Input injection must route through the same paths real input uses (so it faithfully reproduces
movement). Keep the network read off the render hot path or cheap/non-blocking (don't add frame
hitches — poll the socket once per frame, process small messages).

## Safety (hard requirements)

- Bind `127.0.0.1` only; no external interface. Flag-gated; **must be absent/disabled in release
  builds**. Text protocol, no filesystem/shell exposure through commands.

## Acceptance

- With the flag set, the client opens a localhost listener; a manual `nc`/script round-trip of
  `telemetry` returns JSON; `autopilot` drives movement and writes the CSV.
- With the flag unset, no listener opens (verify) — zero behavior change in normal play.
- `godot-build.cmd` clean; `run-checks.cmd` green. Do NOT commit — leave for Orchestrator review.
