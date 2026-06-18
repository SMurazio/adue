# N20 (T4) — MCP wrapper for the client control channel (Phase 2)

Severity: nice-to-have (long-term tooling). Depends on S31 (T2) / S32 (T3). See
`docs/client-control-telemetry-design.md`.

## What

A self-authored, in-repo MCP server that wraps the client's localhost control channel (S31) as agent
tools: `client.move`, `client.stop`, `client.chat`, `client.telemetry`, `client.interp`,
`client.entities`, `client.autopilot`, `client.toggle_fullscreen`, `client.toggle_perf`
(+ `client.screenshot` later). The MCP is a thin adapter over the S31 channel — no new client logic.

This is the long-term interactive interface (autonomous functional/visual testing, reproducing client
bugs without a human, future playtesting).

## Constraints / safety

- **Self-authored only** — no third-party MCP code/prompts. Talks only to our client's `127.0.0.1`
  channel. No external network, no shell/filesystem exposure.
- Document how to register it in Claude Code's MCP config; note that **using it requires a restart**
  (MCP servers load at startup), so it does not help the session that builds it.

## Acceptance

- MCP server builds/runs, exposes the tools, and round-trips commands/telemetry against a running
  flag-enabled client.
- Registration + restart steps documented. Do NOT commit — leave for Orchestrator review.
