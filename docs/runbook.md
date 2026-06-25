# Runbook

## Prerequisites

The .NET 8 SDK ships **in-repo** at `.tools\dotnet\dotnet.exe` — all the commands below invoke it directly,
so a separate machine-wide SDK install is **not required** (the dev scripts fall back to a global `dotnet`
only if the repo-local one is absent). Use the repo-local SDK per the project guardrails.

- **.NET SDK:** repo-local at `.tools\dotnet\dotnet.exe` (no global install needed).
- **Godot 4.7 (.NET / Mono build):** required only for the Godot visual client. See the README for the
  fresh-clone setup. The headless server, tests, and stress tooling do not need Godot.
- **Docker / Postgres:** NOT required. The default database is SQLite (see below); Postgres is an optional
  future path only.

Verify:

```powershell
.\.tools\dotnet\dotnet.exe --list-sdks
git status --short --branch
```

## Database

The default database is SQLite at `data/mmo.db`. The server creates it and applies migrations automatically on startup.

## Run Tests

```powershell
.\.tools\dotnet\dotnet.exe test .\Mmo.sln
```

Agent workflow scripts live under `.shared\skills\mmo-dev\scripts`.

## Stress Test

Start the server first, then run a conservative synthetic-client load:

```powershell
.\.shared\skills\mmo-dev\scripts\stress-test.cmd
```

The stress client opens many LiteNetLib connections, logs in unique local characters, drives movement via held-direction intent (change direction periodically plus a low-rate keepalive), receives snapshots, and reports active peers, authenticated clients, snapshot throughput, protocol bandwidth, latency, and errors.

Useful examples:

```powershell
.\.shared\skills\mmo-dev\scripts\stress-test.cmd --clients=10 --duration=10s
.\.shared\skills\mmo-dev\scripts\stress-test.cmd --clients=100 --duration=60s --spawn-rate=50
.\.shared\skills\mmo-dev\scripts\stress-test.cmd --clients=50 --duration=30s --chat-interval=10s
.\.shared\skills\mmo-dev\scripts\stress-test.cmd --clients=150 --duration=15s --spawn-rate=100
```

The stress tool fails the process if the accepted-login ratio is below `--min-auth-rate` or errors exceed `--max-errors`. Defaults are strict: `--min-auth-rate=1` and `--max-errors=0`.

The default SQLite database will create local load-test characters. Delete `data\mmo.db` when you want a clean local world.

Use Debug stress runs for quick functional checks. Use Release for performance acceptance numbers:

```powershell
.\.shared\skills\mmo-dev\scripts\review-stress-release.cmd --clients=120 --duration=60s
```

The Release review stress command starts a Release server, runs the stress tool in Release, captures `/metrics`, and stops the server afterward.

The near-term channel target is 120-150 connected clients visible in one channel. The server sends reliable entity spawn metadata once, then sends compact unreliable snapshots when visible tile state changes or a heartbeat is due. Between full heartbeat snapshots, incomplete snapshots contain only changed visible tile states and clients merge them into the current visible set.

The server tick loop uses `Stopwatch` deadlines and requests 1 ms Windows timer resolution while running. On Windows this avoids the default coarse timer oversleep that can stretch the nominal 20 Hz / 50 ms tick cadence into uneven 60 ms+ gaps. The loop still polls network events between ticks instead of sleeping for a whole tick interval. The server project enables server GC and concurrent GC. Server logging is asynchronous so the simulation thread never performs console I/O, and the old periodic 10-second tick status log is intentionally removed. Use `/metrics` to watch `gc=gen0/gen1/gen2` counts alongside tick max and drift when validating movement hitches.

## Run Server

```powershell
.\.tools\dotnet\dotnet.exe run --project .\src\Mmo.Server\Mmo.Server.csproj
```

Preferred script launcher:

```powershell
.\.shared\skills\mmo-dev\scripts\start-server.cmd
```

To keep the server window visible and also capture server logs to files:

```powershell
.\.shared\skills\mmo-dev\scripts\start-server.cmd -LogToFile
```

This writes `.run\server.log` and error-only `.run\server.err.log`. Use `-LogPath` and
`-ErrorLogPath` to override those paths.

Useful environment variables:

- `MMO_PORT`
- `MMO_TICK_RATE`
- `MMO_CONNECTION_KEY`
- `MMO_ADMIN_NAMES`: comma-separated local dev admin names; defaults to `Admin`
- `MMO_WORLD_WIDTH_TILES`: tile-grid width; defaults to `128`
- `MMO_WORLD_HEIGHT_TILES`: tile-grid height; defaults to `128`
- `MMO_STEP_COOLDOWN_MS`: per-entity step cooldown; defaults to `140`
- `MMO_PERSISTENCE_CHECKPOINT_SECONDS`: async write-behind checkpoint interval for dirty player tiles; defaults to `15`
- `MMO_INTEREST_RADIUS`: server-side AOI radius in tiles; defaults to `40`
- `MMO_MAX_VISIBLE_ENTITIES`: per-client snapshot budget after AOI sorting; defaults to `150`
- `MMO_SPAWN_DISTRIBUTION`: `distributed` by default; use `clustered` to force worst-case all-visible spawn tests
- `MMO_DEBUG_MOVEMENT`: off by default; set to `1` to log server tick hitches plus watched/admin movement steps
- `MMO_DEBUG_MOVEMENT_WATCH`: optional comma-separated watched names or character ids for movement-step/snapshot trace
- `MMO_DEBUG_MOVEMENT_HITCH_MULTIPLIER`: tick hitch threshold as a multiple of tick interval; defaults to `1.5`
- `MMO_DEBUG_MOVEMENT_TICK_DURATION_MS`: duration-only tick trace threshold; defaults to `15`
- `MMO_GODOT_FRAME_HITCH_MS`: Godot client frame-hitch trace threshold; defaults to `33.3`
- `MMO_DEBUG_CONTROL_PORT`: Godot client only; off by default. When set to a valid port the client opens a localhost-only (`127.0.0.1`) debug control channel driven by `client-control.cmd`. Absent in shipped builds.
- `MMO_SERVER_LOG_FILE`: optional server log file path; set by `start-server.cmd -LogToFile`
- `MMO_SERVER_ERR_LOG_FILE`: optional error-only server log file path; set by `start-server.cmd -LogToFile`
- `MMO_DB_PROVIDER`: `sqlite` by default, `postgres` later
- `MMO_DB`
- `MMO_MIGRATIONS_PATH`

## Run Clients

Godot visual debug client:

```powershell
.\.shared\skills\mmo-dev\scripts\start-godot-visual-check.cmd
```

The Godot client uses the Compatibility (`gl_compatibility`) renderer. Forward+ on D3D12 caused
visible frame hitches from lazy shader/pipeline compilation in this simple 2.5D scene; keep the
Compatibility renderer unless a measured visual requirement justifies revisiting it.
Press `F3` in the Godot client to toggle the performance HUD. It shows FPS, process/physics frame
time, render counts, memory, GC counts, frame hitches, and a rolling frame-time graph.
Static wall tiles render through one `MultiMeshInstance3D`, and entity meshes/materials are reused,
so the Godot client should not create one render object per blocked tile or per entity material.

Isometric 3D browser debug client:

```powershell
.\.tools\dotnet\dotnet.exe run --project .\src\Mmo.Client.Web\Mmo.Client.Web.csproj
```

Open http://127.0.0.1:5080.

Console diagnostic clients:

```powershell
.\.tools\dotnet\dotnet.exe run --project .\src\Mmo.Client.Console\Mmo.Client.Console.csproj -- --name=PlayerOne
.\.tools\dotnet\dotnet.exe run --project .\src\Mmo.Client.Console\Mmo.Client.Console.csproj -- --name=PlayerTwo
```

Client commands:

- `w`, `a`, `s`, `d`: send one tile step, repeated while held
- `w`, `a`, `s`, `d` are screen-relative in the web client; `w` moves up on the isometric view
- hold two movement keys together for diagonals, for example `w` + `d`
- `stop`: stop local repeated movement in clients that support it
- `/say hello`: send chat
- `/quit`: disconnect

The web client also has diagonal movement buttons: `NW`, `NE`, `SW`, and `SE`.
In the 3D web view, hold right mouse button on the ground to move toward the pointer; release to stop.
The web renderer shows the tile grid and blocked wall tiles from the server's `ZoneInfo` message. It tweens local and remote entities only after the server confirms a new tile in a snapshot. The debug visibility ring uses the server-advertised `MMO_INTEREST_RADIUS`. The server owns authoritative tile position; there is no client prediction.

Snapshot logging is off by default. Add `--snapshots` to the client command to print once-per-second world snapshots.

## Movement Debug Trace

Use the headless trace harness when movement visibly stalls and you need a correlated server/client timeline without Godot:

```powershell
.\.shared\skills\mmo-dev\scripts\movement-debug-trace.cmd
```

The harness enables `MMO_DEBUG_MOVEMENT`, runs an in-process server plus two `Mmo.Client.Core` clients, and prints structured `mmo_trace` lines for send -> validate/apply -> snapshot -> confirm. In live Godot runs, setting `MMO_DEBUG_MOVEMENT=1` also adds compact movement and frame fields to the top-left overlay. Godot frame hitches emit `mmo_trace side=client event=frame_hitch` with frame duration, client GC deltas, interpolation queue depth, cadence, latency, visible entity count, and render position. Use those fields to separate client GC, interpolation starvation, and engine/frame-pacing stalls.

## Drive the Godot Client (debug control channel)

The Godot client can open a **localhost-only** debug control channel for driving movement and reading
live state from a script — so the agent can reproduce, profile, and functionally test client behavior
without a human moving the avatar. It is **off by default**, gated by `MMO_DEBUG_CONTROL_PORT`, binds
`127.0.0.1` only, and is absent in shipped builds.

Set the port, then start a Godot client with the channel enabled (same shell):

```powershell
$env:MMO_DEBUG_CONTROL_PORT = 7780
.\.shared\skills\mmo-dev\scripts\start-godot-visual-check.cmd
```

Drive it with `client-control.cmd` (reads `MMO_DEBUG_CONTROL_PORT`, or pass `-Port`):

```powershell
.\.shared\skills\mmo-dev\scripts\client-control.cmd -State
.\.shared\skills\mmo-dev\scripts\client-control.cmd -Telemetry
.\.shared\skills\mmo-dev\scripts\client-control.cmd -Interp
.\.shared\skills\mmo-dev\scripts\client-control.cmd -Entities
.\.shared\skills\mmo-dev\scripts\client-control.cmd -Move N -DurationMs 2000
.\.shared\skills\mmo-dev\scripts\client-control.cmd -Stop
```

Run a scripted autopilot loop, then get a frame-timing summary (worst frames by `frameMs` plus the
dominant `_Process` section: poll / render-state / entities / camera / overlay), backed by
`.run\client-frames.csv`:

```powershell
.\.shared\skills\mmo-dev\scripts\client-control.cmd -Autopilot 20
.\.shared\skills\mmo-dev\scripts\client-control.cmd -Autopilot 30 -Pattern zigzag -Top 12
```

Switches combine (queries first, then move/stop, then autopilot). Use `-Cmd '{...}'` to send a raw
JSON request line for commands without a dedicated switch (`chat`, `toggle_perf`,
`toggle_fullscreen`, `ping`):

```powershell
.\.shared\skills\mmo-dev\scripts\client-control.cmd -Cmd '{"cmd":"chat","text":"hi"}'
```

The protocol is line-delimited JSON: one `{"cmd":"..."}` request line in, one JSON response line out.
The channel never touches the filesystem or shell on behalf of a request; its only disk write is the
autopilot CSV under `.run\`.

### MCP server (T4): drive the client from Claude Code

`tools\mcp\client-control` is a self-authored MCP server (Node.js, official `@modelcontextprotocol/sdk`,
stdio) that wraps the same channel as agent tools — a thin proxy with no new client logic. It connects
only to `127.0.0.1` (loopback, not configurable), opens one fresh connection per request, and exposes:
`client.move`, `client.stop`, `client.chat`, `client.autopilot`, `client.toggle_perf`,
`client.toggle_fullscreen`, `client.telemetry`, `client.interp`, `client.entities`, `client.state`,
`client.ping`. See `tools\mcp\client-control\README.md`.

Install once:

```powershell
cd tools\mcp\client-control
npm install
```

Register it in `.mcp.json` at the repo root (port must match the client's `MMO_DEBUG_CONTROL_PORT`):

```json
{
  "mcpServers": {
    "mmo-client-control": {
      "command": "node",
      "args": ["tools/mcp/client-control/server.js"],
      "env": { "MMO_DEBUG_CONTROL_PORT": "7780" }
    }
  }
}
```

**Restart Claude Code after registering.** MCP servers load at startup, so the tools only appear in a
new session — they cannot help the session that builds/registers them. Start the Godot client with
`MMO_DEBUG_CONTROL_PORT` set (see above) before invoking the tools.

## Dev Admin Commands

Log in as `Admin` in the browser or console client to use local dev admin commands. This is name-based only for local learning and must be replaced with real authentication before exposing the server outside your machine.

Commands are sent as chat messages:

- `/help`: show available commands
- `/role`: show your current role
- `/who`: list authenticated players and latency
- `/metrics`: show server tick, network, login, snapshot, and message counters
- `/stress`: shorthand for `/stress start 120 60s`
- `/stress status`: show in-process synthetic client status
- `/stress start`: spawn the default 120 synthetic clients for 60 seconds
- `/stress start 25 30s`: spawn 25 synthetic clients for 30 seconds
- `/stress stop`: stop the in-process synthetic clients

The server clamps `/stress start` to at most 200 synthetic clients and durations from 5 seconds to 10 minutes.
Use `/metrics` before and during stress runs to spot regressions before they become crashes. Metrics include tick rate, tick duration, schedule drift, category budget buckets (`movement / AOI / serialize / network / persistence / other`), average/max per-client snapshot bytes, total bandwidth, message counts, login timing, and fault counters.

## Reset Local Database

```powershell
Remove-Item .\data\mmo.db
```

## Common Failures

- `No .NET SDKs were found`: invoke the repo-local SDK at `.tools\dotnet\dotnet.exe` (not a global `dotnet`); confirm the `.tools\dotnet` folder is present after clone.
- `docker is not recognized`: ignore unless using the optional Postgres path.
- `SQLite Error 14`: confirm the server can create the `data` directory.
- client never connects: confirm UDP port, host, and `MMO_CONNECTION_KEY`.
