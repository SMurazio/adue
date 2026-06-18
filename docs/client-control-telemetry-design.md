# Client Control & Telemetry — Design

## Goal

Let an external driver (a script now, an MCP later) **control the Godot client and read its live
state**, so the agent can reproduce, profile, and functionally test client behavior autonomously —
without a human manually moving the avatar and reading the on-screen HUD. This is the recurring
blocker: we can't currently observe or drive the client, so debugging is screenshot ping-pong.

## Key architectural decision: the control *surface* is the asset, not the MCP

To control the client, the client must expose a debug interface (inject input + read state). **That
surface is the durable, reusable asset.** An MCP is just one adapter that turns the surface into
agent tools; a skill script is another. So we build the surface as an **open seam**, drivable by
both, and we don't couple the long-term value to the MCP.

```
Godot client ── debug control channel (localhost) ──┬── skill script (now: profile/repro/test)
                (input inject + telemetry readout)   └── MCP server (later: interactive tools)
```

## Phase 1 — Control surface + telemetry (unblocks immediately, script-driven)

Client-side debug control channel, **off by default**, enabled by a debug flag (e.g.
`MMO_DEBUG_CONTROL_PORT`). When enabled the client opens a **localhost-only** TCP listener
(`127.0.0.1`) speaking a minimal line-delimited JSON request/response protocol.

**Commands (inject):**
- `move {dir, durationMs|steps}` / `stop` — drive movement.
- `chat {text}`, `toggle_perf`, `toggle_fullscreen`.
- `autopilot {pattern, durationMs}` — run a scripted movement loop (for controlled, repeatable repro).

**Queries (read):**
- `telemetry` — `fps`, frame ms (last/max/avg), **per-`_Process`-section timing** (poll / render-state
  / entities / camera / overlay), gc gen0/1/2 deltas, hitch count.
- `interp` — queueDepth, cadence, confirmed tile, latency.
- `entities` — network id, tile, render position per entity.
- `state` — connection/login/role/zone.

**Telemetry sink for profiling:** an `autopilot` run also appends per-frame rows to a CSV in `.run/`
(frame ms + per-section timing + gc deltas), so the driver can pull a full trace and find exactly
which frames spiked and which section caused them.

**Driver (now):** a skill script (`client-control.cmd`) that connects to the channel, sends a
command sequence (e.g. autopilot 30s), and dumps the telemetry/CSV for the agent to read. No MCP, no
restart needed — usable this session.

## Phase 2 — MCP wrapper (long-term interactive interface)

An MCP server (self-authored, in-repo) that wraps the same channel as tools: `client.move`,
`client.stop`, `client.telemetry`, `client.entities`, `client.autopilot`, `client.screenshot`,
`client.toggle_fullscreen`. Registered in Claude Code's MCP config (requires a restart to load).
Reuses the Phase-1 surface verbatim — the MCP is a thin adapter.

Long-term uses: autonomous functional testing (drive scenarios, assert state), visual/integration
testing (screenshot), reproducing client bugs without a human, future playtesting of AI/combat.

## Safety model (per project rule: no inbound access, no untrusted code)

- **Localhost-only:** bind `127.0.0.1`, never `0.0.0.0`. No remote can connect.
- **Debug-flag-gated:** no listener unless the debug flag is set; **absent in release/shipped builds.**
- **Self-authored MCP only** — no third-party MCP with unknown code or prompts. The MCP only ever
  talks to our own client's localhost channel.
- Text protocol, minimal surface, no filesystem/shell exposure through the channel.

## Implementation breakdown (sub-agent-sized todos)

1. **T1 — per-`_Process` section timing** in `MmoClientRoot` (poll/render-state/entities/camera/
   overlay), surfaced in the F3 HUD and the telemetry payload. (Smallest; also directly useful for the
   current hitch hunt.)
2. **T2 — control channel**: localhost listener + command/query protocol + `autopilot` + CSV telemetry
   sink, flag-gated. Depends on T1's telemetry.
3. **T3 — `client-control` skill script** to drive the channel and dump telemetry.
4. **T4 — MCP server** wrapping the channel (Phase 2), + config/registration notes.

Each is self-contained with build/`run-checks`/`godot-build` acceptance, suitable for an isolated
sub-agent. Human verification (feel) and live profiling happen after, using the new surface.
