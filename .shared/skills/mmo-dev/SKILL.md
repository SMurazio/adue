---
name: mmo-dev
description: Project-specific MMO development workflows for D:\MMO. Use when Codex needs to run, stop, verify, debug, or explain this MMO repo, including starting the .NET server, starting the browser debug client, running build/tests, resetting SQLite, checking logs, or avoiding Docker/Postgres assumptions.
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

`godot-run` needs `MMO_GODOT` set to the Godot .NET executable (or `godot` on PATH):

```powershell
setx MMO_GODOT "D:\Tools\Godot\Godot_v4.6.3-stable_mono_win64.exe"
```

These cover the automatable checks (does the C# compile, does it start/run/log without crashing). The
**visual/feel** check still needs a human in the Godot editor. This is the deliberately
token-cheap alternative to a Godot MCP — a fixed script the agent runs, not a tool-schema tax every
turn.

## Local Runtime Facts

- Game server: UDP `127.0.0.1:7777`.
- Browser debug client: http://127.0.0.1:5080.
- SQLite database: `data/mmo.db`.
- PID files: `.run/server.pid`, `.run/web-client.pid`.
- Godot client project: `src/Mmo.Client.Godot` (`MmoClientGodot.sln`); Godot 4.x .NET build.
- `MMO_GODOT` env var: path to the Godot .NET executable (used by `godot-run.cmd`).

## Rules

- Do not require Docker for normal local development.
- Stop running server/client processes before a full solution build if DLLs are locked.
- Prefer `start-server.cmd` and `start-web-client.cmd`; they run visible windows with PID files.
- Use `stop-mmo.cmd` before restarting server/web; it cleans PID files, port listeners, repo-local dotnet, and known wrapper windows.
- Use `--snapshots` only when the console client needs snapshot logs.
- Use `stress-test.cmd` for synthetic client load before trying manual multi-window testing.
- Use `start-godot-visual-check.cmd` when the S16 Godot visual/manual check is needed.
- Keep Postgres as a later provider, not the default path.
