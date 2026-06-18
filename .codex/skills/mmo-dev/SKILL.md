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
.\.codex\skills\mmo-dev\scripts\run-checks.cmd
```

Run a conservative local stress test against the current server:

```powershell
.\.codex\skills\mmo-dev\scripts\stress-test.cmd
```

Pass through load settings when needed:

```powershell
.\.codex\skills\mmo-dev\scripts\stress-test.cmd --clients=100 --duration=60s --spawn-rate=50
```

Start the MMO server in the background:

```powershell
.\.codex\skills\mmo-dev\scripts\start-server.cmd
```

Start the browser debug client in the background:

```powershell
.\.codex\skills\mmo-dev\scripts\start-web-client.cmd
```

The default start scripts open visible console windows and record PID files:

```powershell
.\.codex\skills\mmo-dev\scripts\start-server.cmd
.\.codex\skills\mmo-dev\scripts\start-web-client.cmd
```

Stop background processes and leftover MMO windows started by these scripts:

```powershell
.\.codex\skills\mmo-dev\scripts\stop-mmo.cmd
```

Preview what would be stopped without killing anything:

```powershell
.\.codex\skills\mmo-dev\scripts\stop-mmo.cmd -DryRun
```

## Local Runtime Facts

- Game server: UDP `127.0.0.1:7777`.
- Browser debug client: http://127.0.0.1:5080.
- SQLite database: `data/mmo.db`.
- PID files: `.run/server.pid`, `.run/web-client.pid`.

## Rules

- Do not require Docker for normal local development.
- Stop running server/client processes before a full solution build if DLLs are locked.
- Prefer `start-server.cmd` and `start-web-client.cmd`; they run visible windows with PID files.
- Use `stop-mmo.cmd` before restarting server/web; it cleans PID files, port listeners, repo-local dotnet, and known wrapper windows.
- Use `--snapshots` only when the console client needs snapshot logs.
- Use `stress-test.cmd` for synthetic client load before trying manual multi-window testing.
- Keep Postgres as a later provider, not the default path.
