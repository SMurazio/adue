---
name: mmo-dev
description: Project-specific MMO development workflows for D:\MMO. Use when Claude needs to run, stop, verify, debug, or explain this MMO repo, including starting the .NET server, starting the browser debug client, running build/tests, resetting SQLite, checking logs, or avoiding Docker/Postgres assumptions.
---

# MMO Dev

Use the repo-local .NET SDK at `.tools/dotnet/dotnet.exe`.

Prefer the browser debug client over the console client for interactive testing. The console client is still useful for scripted protocol checks.

## Workflows

Run build and tests:

```powershell
.\.shared\skills\mmo-dev\scripts\run-checks.cmd
```

Run a conservative local stress test against the current server:

```powershell
.\.shared\skills\mmo-dev\scripts\stress-test.cmd
```

Pass through load settings when needed:

```powershell
.\.shared\skills\mmo-dev\scripts\stress-test.cmd --clients=100 --duration=60s --spawn-rate=50
```

Run review/performance stress numbers in Release:

```powershell
.\.shared\skills\mmo-dev\scripts\review-stress-release.cmd --clients=120 --duration=60s
```

Start the MMO server in the background:

```powershell
.\.shared\skills\mmo-dev\scripts\start-server.cmd
```

Start the server with file logs while keeping the normal visible server window:

```powershell
.\.shared\skills\mmo-dev\scripts\start-server.cmd -LogToFile
```

This writes `.run/server.log` and `.run/server.err.log`.

Start the browser debug client in the background:

```powershell
.\.shared\skills\mmo-dev\scripts\start-web-client.cmd
```

The default start scripts open visible console windows and record PID files:

```powershell
.\.shared\skills\mmo-dev\scripts\start-server.cmd
.\.shared\skills\mmo-dev\scripts\start-web-client.cmd
```

Stop background processes and leftover MMO windows started by these scripts:

```powershell
.\.shared\skills\mmo-dev\scripts\stop-mmo.cmd
```

Preview what would be stopped without killing anything:

```powershell
.\.shared\skills\mmo-dev\scripts\stop-mmo.cmd -DryRun
```

## Godot Client

Compile the Godot client's C# (no editor / no Godot launch — pure repo-local dotnet):

```powershell
.\.shared\skills\mmo-dev\scripts\godot-build.cmd
```

Run the Godot client headless for ~N seconds and capture its output (compile + runtime smoke):

```powershell
.\.shared\skills\mmo-dev\scripts\godot-run.cmd 8
```

Launch the manual Godot visual check: visible server plus two visible Godot clients named `GodotA`
and `GodotB`:

```powershell
.\.shared\skills\mmo-dev\scripts\start-godot-visual-check.cmd
```

Add `-LogToFile` to capture the server logs during that visual check.
Inside the Godot client, press `F3` to toggle the performance HUD with FPS, frame timings, render
counts, memory, GC, hitch count, and a rolling frame-time graph.

`godot-run` needs `MMO_GODOT` set to the Godot .NET executable (or `godot` on PATH):

```powershell
setx MMO_GODOT "D:\Tools\Godot\Godot_v4.6.3-stable_mono_win64.exe"
```

These cover the automatable checks (does the C# compile, does it start/run/log without crashing). The
**visual/feel** check still needs a human in the Godot editor. This is the deliberately
token-cheap alternative to a Godot MCP — a fixed script the agent runs, not a tool-schema tax every
turn.

## Driving the Godot client (debug control channel)

The Godot client can expose a **localhost-only** debug control channel when started with the
`MMO_DEBUG_CONTROL_PORT` environment variable set (off by default; absent in shipped builds). When
enabled, `client-control.cmd` connects to it and lets the agent drive movement and read live state
without a human moving the avatar — the immediate unblock for profiling the residual hitch.

Set the port and start a Godot client with the channel enabled (in the same shell):

```powershell
$env:MMO_DEBUG_CONTROL_PORT = 7780
.\.shared\skills\mmo-dev\scripts\start-godot-visual-check.cmd
```

Then drive it (the script reads `MMO_DEBUG_CONTROL_PORT`, or pass `-Port`):

```powershell
.\.shared\skills\mmo-dev\scripts\client-control.cmd -State          # connection/login/role/zone/tile
.\.shared\skills\mmo-dev\scripts\client-control.cmd -Telemetry      # fps, frame ms, per-section ms, gc, hitches
.\.shared\skills\mmo-dev\scripts\client-control.cmd -Interp         # queue depth, cadence, confirmed tile, latency
.\.shared\skills\mmo-dev\scripts\client-control.cmd -Entities       # network id / tile / render pos per entity
.\.shared\skills\mmo-dev\scripts\client-control.cmd -Move N -DurationMs 2000
.\.shared\skills\mmo-dev\scripts\client-control.cmd -Stop
```

Run a scripted autopilot loop and get a frame-timing summary (worst frames + dominant `_Process`
section), backed by `.run/client-frames.csv`:

```powershell
.\.shared\skills\mmo-dev\scripts\client-control.cmd -Autopilot 20            # 20s, default 'square'
.\.shared\skills\mmo-dev\scripts\client-control.cmd -Autopilot 30 -Pattern zigzag -Top 12
```

Switches combine (queries run first, then move/stop, then autopilot). `-Cmd '{...}'` sends a raw
JSON request line for commands without a dedicated switch (`chat`, `toggle_perf`,
`toggle_fullscreen`, `ping`):

```powershell
.\.shared\skills\mmo-dev\scripts\client-control.cmd -Cmd '{"cmd":"chat","text":"hi"}'
```

The channel binds `127.0.0.1` only and never touches the filesystem/shell on behalf of a request —
its only disk write is the autopilot CSV under `.run/`.

## Local Runtime Facts

- Game server: UDP `127.0.0.1:7777`.
- Browser debug client: http://127.0.0.1:5080.
- SQLite database: `data/mmo.db`.
- PID files: `.run/server.pid`, `.run/web-client.pid`.
- Godot client project: `src/Mmo.Client.Godot` (`MmoClientGodot.sln`); Godot 4.x .NET build.
- `MMO_GODOT` env var: path to the Godot .NET executable (used by `godot-run.cmd`).
- `MMO_DEBUG_CONTROL_PORT` env var: Godot client only, off by default; when set, the client opens a localhost-only debug control channel that `client-control.cmd` drives.
- Godot client control CSV: `.run/client-frames.csv` (written by an `autopilot` run).

## Rules

- Do not require Docker for normal local development.
- Stop running server/client processes before a full solution build if DLLs are locked.
- Prefer `start-server.cmd` and `start-web-client.cmd`; they run visible windows with PID files.
- Use `stop-mmo.cmd` before restarting server/web; it cleans PID files, port listeners, repo-local dotnet, and known wrapper windows.
- Use `--snapshots` only when the console client needs snapshot logs.
- Use `stress-test.cmd` for synthetic client load before trying manual multi-window testing.
- Use `start-godot-visual-check.cmd` when the S16 Godot visual/manual check is needed.
- Keep Postgres as a later provider, not the default path.
