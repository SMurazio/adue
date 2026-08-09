---
name: mmo-dev
description: Dev workflows for the ADUE repo (D:\Adue, forked from the MMO). Use to run, stop, verify, debug, or explain this repo — start the .NET server, launch the Godot client(s) for the duo feel-test, run build/tests, stress, or profiling. Retains MMO-era framing (SQLite, web client) for parked systems.
---

# Adue Dev (forked from mmo-dev)

> **ADUE ORIENTATION.** This repo is Adue — the standalone two-player co-op roguelite — forked
> full-history from the MMO. The skill name stays `mmo-dev` (referenced by the stub); its scripts
> split into:
> - **Duo-primary (live):** `run-checks`, `start-server`, `start-godot-visual-check` (SOLO client),
>   **`start-duo`** (TWO clients — the two-player feel-test / merge gate), `stop-mmo`,
>   `godot-build/import/run`, `client-control`, `movement-debug-trace`.
> - **Perf gate (keep):** `stress-test`, `review-stress`, `review-stress-release` — the plan keeps
>   these as the performance gate; not cruft.
> - **Online-duo relevant:** `connect-server` — the two-machine LAN path the bundled host-side
>   server work will reuse.
> - **MMO-era / parked:** the web-client trio (`start-web-client`, `run-web-client-window`,
>   `start-web-client.ps1`) drives the retired tile-stepped browser client — slated for retirement,
>   see `todo/N-retire-web-client.md`; do NOT use it for Adue testing. SQLite/ecology/AOI framing
>   below is likewise parked (prune on friction, not principle).

Use the repo-local .NET SDK at `.tools/dotnet/dotnet.exe`.

The **Godot client** is the client for Adue. The browser/console clients are MMO-era; the console
client is still occasionally useful for scripted protocol checks.

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

Launch the **duo feel-test** — visible server plus TWO Godot clients (`GodotA` + `GodotB`), the
two-player run-loop / merge-gate check:

```powershell
.\.shared\skills\mmo-dev\scripts\start-duo.cmd
```

For a **solo** run, launch a single client instead:

```powershell
.\.shared\skills\mmo-dev\scripts\start-godot-visual-check.cmd
```

Both wrap `start-godot-visual-check.ps1` (solo passes `-Clients 1`, `start-duo` passes `-Clients 2`).
Add `-LogToFile` to either to capture the server logs during the check.
Inside the Godot client, press `F3` to toggle the performance HUD with FPS, frame timings, render
counts, memory, GC, hitch count, and a rolling frame-time graph.

## LAN / side-by-side play (two machines, one server)

One machine HOSTS the server; the other JOINS it. The server already binds to all interfaces (`0.0.0.0` via
`NetManager.Start(port)`), so LAN play is configuration only — no code change.

Host (the machine running the server):

1. Start the server normally — `start-server.cmd` (or `start-godot-visual-check.cmd` to also play locally).
2. Open inbound UDP on the server port (default 7777) in Windows Firewall — needs admin, and must be permitted
   by any central/company firewall policy:

   ```powershell
   New-NetFirewallRule -DisplayName "MMO Server UDP 7777" -Direction Inbound -Action Allow -Protocol UDP -LocalPort 7777
   ```

3. Tell the joiner the host's LAN IPv4 (`ipconfig` -> IPv4 Address).

Joiner (the other machine, same LAN): launch ONE client pointed at the host — NO local server:

```powershell
.\.shared\skills\mmo-dev\scripts\connect-server.cmd                       # joins the default host IP in the script
.\.shared\skills\mmo-dev\scripts\connect-server.cmd -Server 192.168.1.42 -Name Bob
```

`connect-server` builds the client, then launches a single Godot client with `MMO_HOST` set to the host
(default in the script). It uses the shared `local-dev` connection key, which must match the host server's.
Stop with `stop-mmo.cmd`.

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
- Browser debug client: http://127.0.0.1:5080 (MMO-era / parked — see `todo/N-retire-web-client.md`).
- SQLite database: `data/mmo.db` (MMO-era / parked).
- PID files: `.run/server.pid`, `.run/web-client.pid`.
- Godot client project: `src/Mmo.Client.Godot` (`MmoClientGodot.sln`); Godot 4.x .NET build.
- `MMO_GODOT` env var: path to the Godot .NET executable (used by `godot-run.cmd`).
- `MMO_DEBUG_CONTROL_PORT` env var: Godot client only, off by default; when set, the client opens a localhost-only debug control channel that `client-control.cmd` drives.
- Godot client control CSV: `.run/client-frames.csv` (written by an `autopilot` run).

## Rules

- Do not require Docker for normal local development.
- Stop running server/client processes before a full solution build if DLLs are locked.
- Prefer `start-server.cmd` + the Godot launchers (`start-duo.cmd` for the two-player test,
  `start-godot-visual-check.cmd` for solo); they run visible windows with PID files. `start-web-client.cmd`
  is MMO-era/parked — don't use it for Adue.
- Use `stop-mmo.cmd` before restarting server/clients; it cleans PID files, port listeners, repo-local dotnet, and known wrapper windows.
- Use `--snapshots` only when the console client needs snapshot logs.
- Use `stress-test.cmd` for synthetic client load before trying manual multi-window testing.
- Use `start-duo.cmd` (two clients) for the Adue duo feel-test; `start-godot-visual-check.cmd` (one client) for solo.
- Keep Postgres as a later provider, not the default path.
