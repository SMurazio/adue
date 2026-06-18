# MMO Learning Project

This repo is a from-scratch, production-shaped learning project for a small 2D top-down MMO server/client.

The first target is not a full game. The first target is the MMO spine:

- authoritative fixed-tick server
- reliable UDP transport with LiteNetLib
- SQLite persistence now, with a Postgres path kept for later
- one shared zone
- login, spawn, movement, visibility snapshots, and chat
- a simple diagnostic console client before any Godot client work

## Current Prerequisites

Install these before building locally:

- .NET 8 SDK
- Git

This repo also supports a local SDK installed at `.tools/dotnet`.

Docker is optional for now. It is only needed later if you switch the database provider to Postgres.

## First Local Run

```powershell
.\.tools\dotnet\dotnet.exe restore .\Mmo.sln
.\.tools\dotnet\dotnet.exe test .\Mmo.sln
.\.tools\dotnet\dotnet.exe run --project .\src\Mmo.Server\Mmo.Server.csproj
```

In another terminal:

```powershell
.\.tools\dotnet\dotnet.exe run --project .\src\Mmo.Client.Console\Mmo.Client.Console.csproj -- --name=PlayerOne
```

Run a second client with another name to test visibility and chat.

The default database is `data/mmo.db`. Delete that file to reset local game data.

## Dev Admin Commands

This repo has local-development roles for debugging. By default, logging in with the name `Admin` grants the `Admin` role. Override the comma-separated allowlist with `MMO_ADMIN_NAMES`.

Admin slash commands are sent through chat:

```text
/help
/role
/who
/metrics
/stress
/stress status
/stress start 25 30s
/stress stop
```

`/stress` is shorthand for `/stress start 120 60s`. `/stress start` spawns bounded in-process synthetic clients that connect back to the server through LiteNetLib. It is useful for watching the web client under load without opening many browser windows. This is dev tooling, not a production auth model.

Snapshot logging is off by default. To print once-per-second world snapshots in the diagnostic client, add `--snapshots`:

```powershell
.\.tools\dotnet\dotnet.exe run --project .\src\Mmo.Client.Console\Mmo.Client.Console.csproj -- --name=PlayerOne --snapshots
```

For an isometric 3D browser debug client, start the web bridge:

```powershell
.\.tools\dotnet\dotnet.exe run --project .\src\Mmo.Client.Web\Mmo.Client.Web.csproj
```

Then open http://127.0.0.1:5080.

To stress test the server with synthetic LiteNetLib clients, keep the server running and run:

```powershell
.\.shared\skills\mmo-dev\scripts\stress-test.cmd
```

For a larger run:

```powershell
.\.shared\skills\mmo-dev\scripts\stress-test.cmd --clients=100 --duration=60s --spawn-rate=50
```

The stress tool defaults to strict pass criteria: every spawned client must authenticate and server/network errors must stay at zero.

## Useful Docs

- [LLM handoff plan](MMO_PROJECT_PLAN.md)
- [Feature roadmap](docs/feature-roadmap.md)
- [Architecture](docs/architecture.md)
- [Protocol](docs/protocol.md)
- [Runbook](docs/runbook.md)
- [Reference study: Godot Tiny MMO](docs/reference-study-godot-tiny-mmo.md)
- [Multiplayer networking references](docs/networking-references.md)
- [Networking reference catalogue (depth-annotated)](docs/networking-reference-catalogue.md)
- [Networking design plan (extrapolated)](docs/networking-design-plan.md)
- [WorldState / Zone design](docs/worldstate-zone-design.md)
- [Godot client design](docs/godot-client-design.md)

## Agent Skill

Repo-local workflow skill: `.shared/skills/mmo-dev/SKILL.md`.

New project skills should use the shared layout: put the canonical skill under
`.shared/skills/<skill-name>/`, then add thin discovery stubs for each agent under `.codex/skills/`
and `.claude/skills/`.

Useful scripts:

```powershell
.\.shared\skills\mmo-dev\scripts\start-server.cmd
.\.shared\skills\mmo-dev\scripts\start-web-client.cmd
.\.shared\skills\mmo-dev\scripts\stress-test.cmd
.\.shared\skills\mmo-dev\scripts\stop-mmo.cmd
.\.shared\skills\mmo-dev\scripts\run-checks.cmd
```

To preview cleanup targets before stopping server/web processes:

```powershell
.\.shared\skills\mmo-dev\scripts\stop-mmo.cmd -DryRun
```

The default start scripts open visible console windows and `stop-mmo.cmd` closes them through PID files. The older direct window helpers are still available for manual debugging:

```powershell
.\.shared\skills\mmo-dev\scripts\run-server-window.cmd
.\.shared\skills\mmo-dev\scripts\run-web-client-window.cmd
```
