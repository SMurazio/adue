# mmo-client-control MCP server (T4)

Self-authored MCP server that wraps the Godot client's **localhost debug control channel**
(`src/Mmo.Client.Godot/DebugControlChannel.cs`) as agent tools. It is the Phase-2 interactive adapter
described in `docs/client-control-telemetry-design.md`; the durable asset is the channel, and this
server is a **thin proxy** over it — no client logic lives here.

## What it does

For every request it opens one fresh TCP connection to `127.0.0.1:<port>`, sends a single
line-delimited JSON request (`{"cmd":"..."}`), reads the one JSON response line, and closes — exactly
like the T3 `client-control.ps1` script. The channel's JSON (including `{"ok":false,"error":...}`) is
returned verbatim.

### Tools

| Tool | Channel cmd | Notes |
| --- | --- | --- |
| `client.move` | `move` | `dir` (N/NE/E/SE/S/SW/W/NW), optional `durationMs` |
| `client.stop` | `stop` | |
| `client.chat` | `chat` | `text` (also accepts slash commands like `/who`) |
| `client.autopilot` | `autopilot` | optional `pattern` (square/line/zigzag/circle), optional `durationMs` (default 30000); appends to `.run/client-frames.csv` |
| `client.toggle_perf` | `toggle_perf` | |
| `client.toggle_fullscreen` | `toggle_fullscreen` | |
| `client.telemetry` | `telemetry` | fps, frame ms, per-section timing, gc deltas, hitch count |
| `client.interp` | `interp` | queueDepth, cadence, confirmed tile, latency |
| `client.entities` | `entities` | per-entity networkId, tile, render x/y |
| `client.state` | `state` | connection/login/role/zone |
| `client.ping` | `ping` | reachability probe |

## Safety

- **Loopback only.** The host is the hard-coded literal `127.0.0.1` and is **not** configurable, so
  this process can never reach a remote machine.
- **No other capability.** No shell, no filesystem access, no external network. It only opens a
  short-lived socket to the local control channel.
- **Debug-gated upstream.** The channel itself only exists when the client was started with
  `MMO_DEBUG_CONTROL_PORT` set, binds `127.0.0.1` exclusively, and is absent in shipped builds.
- **Self-authored only** — no third-party MCP code or prompts.

## Install

```powershell
cd tools\mcp\client-control
npm install
node --check server.js   # syntax check
```

## Run / port resolution

The server resolves the channel port in this order:

1. `--port <n>` (or `--port=<n>`) CLI arg
2. `MMO_DEBUG_CONTROL_PORT` environment variable

There is no silent default — start the client with `MMO_DEBUG_CONTROL_PORT` set, then register this
server with the **same** port.

```powershell
# the client must already be running with this port:
$env:MMO_DEBUG_CONTROL_PORT = 7780
node tools\mcp\client-control\server.js          # uses the env var
node tools\mcp\client-control\server.js --port 7780
```

## Register in Claude Code

MCP servers are loaded by Claude Code **at startup**. Add an entry to `.mcp.json` at the repo root (or
your user/global MCP config). Use an absolute path or one relative to the repo root; pass the channel
port via the `MMO_DEBUG_CONTROL_PORT` env so it matches the running client.

```json
{
  "mcpServers": {
    "mmo-client-control": {
      "command": "node",
      "args": ["tools/mcp/client-control/server.js"],
      "env": {
        "MMO_DEBUG_CONTROL_PORT": "7780"
      }
    }
  }
}
```

> **Restart required.** Claude Code loads MCP servers at startup. After adding/editing this entry you
> must **restart Claude Code** for the tools to appear. (This is why T4 cannot help the session that
> builds it — it only becomes usable in a later session.)

The tools then appear as `mmo-client-control` server tools (`client.move`, `client.telemetry`, ...).
Start the Godot client with the channel enabled before invoking them; see `docs/runbook.md` →
"Drive the Godot Client".
