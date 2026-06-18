# Runbook

## Prerequisites

Install:

- .NET 8 SDK
- Docker Desktop only if you want the optional Postgres path later

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

Agent workflow scripts live under `.codex\skills\mmo-dev\scripts`.

## Stress Test

Start the server first, then run a conservative synthetic-client load:

```powershell
.\.codex\skills\mmo-dev\scripts\stress-test.cmd
```

The stress client opens many LiteNetLib connections, logs in unique local characters, sends `MoveStep` inputs, receives snapshots, and reports active peers, authenticated clients, snapshot throughput, protocol bandwidth, latency, and errors.

Useful examples:

```powershell
.\.codex\skills\mmo-dev\scripts\stress-test.cmd --clients=10 --duration=10s
.\.codex\skills\mmo-dev\scripts\stress-test.cmd --clients=100 --duration=60s --spawn-rate=50
.\.codex\skills\mmo-dev\scripts\stress-test.cmd --clients=50 --duration=30s --chat-interval=10s
.\.codex\skills\mmo-dev\scripts\stress-test.cmd --clients=150 --duration=15s --spawn-rate=100
```

The stress tool fails the process if the accepted-login ratio is below `--min-auth-rate` or errors exceed `--max-errors`. Defaults are strict: `--min-auth-rate=1` and `--max-errors=0`.

The default SQLite database will create local load-test characters. Delete `data\mmo.db` when you want a clean local world.

The near-term channel target is 120-150 connected clients visible in one channel. The server sends reliable entity spawn metadata once, then sends compact unreliable snapshots when visible tile state changes or a heartbeat is due. Between full heartbeat snapshots, incomplete snapshots contain only changed visible tile states and clients merge them into the current visible set.

## Run Server

```powershell
.\.tools\dotnet\dotnet.exe run --project .\src\Mmo.Server\Mmo.Server.csproj
```

Useful environment variables:

- `MMO_PORT`
- `MMO_TICK_RATE`
- `MMO_CONNECTION_KEY`
- `MMO_ADMIN_NAMES`: comma-separated local dev admin names; defaults to `Admin`
- `MMO_WORLD_WIDTH_TILES`: tile-grid width; defaults to `128`
- `MMO_WORLD_HEIGHT_TILES`: tile-grid height; defaults to `128`
- `MMO_STEP_COOLDOWN_MS`: per-entity step cooldown; defaults to `140`
- `MMO_INTEREST_RADIUS`: server-side AOI radius in tiles; defaults to `40`
- `MMO_MAX_VISIBLE_ENTITIES`: per-client snapshot budget after AOI sorting; defaults to `150`
- `MMO_SPAWN_DISTRIBUTION`: `distributed` by default; use `clustered` to force worst-case all-visible spawn tests
- `MMO_DB_PROVIDER`: `sqlite` by default, `postgres` later
- `MMO_DB`
- `MMO_MIGRATIONS_PATH`

## Run Clients

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

- `No .NET SDKs were found`: install the .NET 8 SDK, not just the runtime.
- `docker is not recognized`: ignore unless using the optional Postgres path.
- `SQLite Error 14`: confirm the server can create the `data` directory.
- client never connects: confirm UDP port, host, and `MMO_CONNECTION_KEY`.
