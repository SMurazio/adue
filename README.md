# MMO Learning Project

[![CI](https://github.com/SMurazio/mmo/actions/workflows/ci.yml/badge.svg)](https://github.com/SMurazio/mmo/actions/workflows/ci.yml)

This repo is a from-scratch, production-shaped learning project for a small 2D top-down MMO server/client.

The first target is not a full game. The first target is the MMO spine:

- authoritative fixed-tick server
- reliable UDP transport with LiteNetLib
- SQLite persistence now, with a Postgres path kept for later
- one shared zone
- login, spawn, movement, visibility snapshots, and chat
- diagnostic clients (console + isometric web debug client) and a **Godot 4 (C#) 3D client**

Movement is tile-stepped and server-authoritative; the local player runs client-side prediction +
reconciliation. The active focus is a packet-loss / latency-robust input model (sequenced redundant-unreliable
input + authored-tick server processing) — see `docs/movement-netcode-redesign-plan.md`.

## Getting Started (fresh clone)

> **For agents:** this is a step-by-step setup you can walk a user through. After each step, run the **Verify**
> line and confirm the expected output before continuing. The project is two halves: a **.NET 8** server +
> console/web clients (the `Mmo.sln` solution) and a **Godot 4.7 (C#)** client (`src/Mmo.Client.Godot`, a
> separate solution that Godot generates on first open).

### 1. Install the tools

| Tool | What / where | Verify |
|---|---|---|
| **Git + Git LFS** | [git-scm.com](https://git-scm.com); run `git lfs install` once | `git --version`; `git lfs version` |
| **.NET 8 SDK** | [dotnet.microsoft.com/download](https://dotnet.microsoft.com/download) (the **8.0** SDK, not just the runtime) | `dotnet --version` → `8.0.x` |
| **Godot 4.7 — .NET build** | [godotengine.org/download](https://godotengine.org/download) → **Godot 4.7**, the **".NET" / C#** download. The plain build will NOT work — the client is C#. | Launch it; *Editor → Help → About* shows `4.7` with `.NET/Mono` |

Match Godot **4.7** exactly (the project pins it; a different 4.x may force a project upgrade). Docker is optional
(only for the later Postgres path).

### 2. Clone

```bash
git clone https://github.com/SMurazio/mmo.git
cd mmo
git lfs pull        # fetch LFS art (harmless no-op today)
```

### 3. Build + test the .NET side

From the repo root (uses your globally-installed .NET 8):

```bash
dotnet restore Mmo.sln
dotnet build Mmo.sln --no-incremental
dotnet test  Mmo.sln --no-build
```

**Verify:** all three test projects pass (Mmo.Shared.Tests / Mmo.Client.Core.Tests / Mmo.Server.Tests). This is
exactly what CI runs.

### 4. Open the Godot client once (generates per-machine build files)

- Launch the **Godot 4.7 .NET** editor → **Import** → select `src/Mmo.Client.Godot/project.godot` → *Import & Edit*.
- Let it import assets (first time takes a moment). This also generates the C# build files and the gitignored
  `.godot/` cache.
- Build the C# (the editor builds on Play, or press the **Build** / hammer button).

**Verify:** no import or build errors in the Godot editor.

### 5. Run it

1. **Start the server** (a terminal): `dotnet run --project src/Mmo.Server/Mmo.Server.csproj`
   **Verify:** the log prints `Server listening on UDP 7777`.
2. **Run the client:** press **Play** in the Godot editor.
   **Verify:** an avatar appears and moves; the server log shows a login. The client connects to `127.0.0.1:7777`
   with key `local-dev` (defaults match the server); override the host with the `MMO_HOST` env var.
3. **Headless smoke test** (no Godot, optional):
   `dotnet run --project src/Mmo.Client.Console/Mmo.Client.Console.csproj -- --name=PlayerOne`

The default DB is `data/mmo.db`; delete it to reset local game data.

> **Maintainer convenience scripts:** `.shared/skills/mmo-dev/scripts/` (`run-checks`, `start-server`,
> `start-godot-visual-check`, `stop-mmo`, `godot-build`) wrap the above — but they currently expect a repo-local
> SDK at `.tools/dotnet`, which is gitignored and **not** in a fresh clone. On a fresh clone, use the `dotnet` CLI
> commands above; the scripts are a convenience for the maintainer's setup.

The default database is `data/mmo.db`. Delete that file to reset local game data.

## Working on the repo (no branch protection yet)

`main` is **not branch-protected** right now — anyone with access can push to it, and **CI runs _after_ a push, so
it will not block a broken commit from landing.** Until protection is enabled, please be mindful:

- **Run `run-checks` (build + all tests) locally before pushing to `main`** — don't push red.
- **Build and run the Godot client locally** for any client change — CI does **not** compile or run the Godot side
  (`Mmo.sln` excludes it), so your local run is the only gate there.
- **`git pull` before you push** to avoid clobbering each other.
- For anything non-trivial, prefer a **feature branch + PR** so CI runs on it before it reaches `main`.

We'll likely turn on branch protection (require the CI check + a PR) once more than one person is committing regularly.

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
`.shared/skills/<skill-name>/`, then add a thin discovery stub under `.claude/skills/`.

Useful scripts:

```powershell
.\.shared\skills\mmo-dev\scripts\start-server.cmd
.\.shared\skills\mmo-dev\scripts\start-godot-visual-check.cmd
.\.shared\skills\mmo-dev\scripts\start-web-client.cmd
.\.shared\skills\mmo-dev\scripts\stress-test.cmd
.\.shared\skills\mmo-dev\scripts\stop-mmo.cmd
.\.shared\skills\mmo-dev\scripts\run-checks.cmd   # build + tests (also runs in CI)
.\.shared\skills\mmo-dev\scripts\godot-build.cmd  # compile the Godot client (not in CI)
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
