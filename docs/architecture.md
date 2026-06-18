# Architecture

## Shape

The server is authoritative. Clients send intent, not final state. The server loop polls network events, applies validated step inputs, runs a fixed tick, and sends snapshots when visible state changes or a heartbeat is due.

```text
Console Client / Future Godot Client
        |
        | LiteNetLib UDP messages
        v
Mmo.Server
  - connection/session lifecycle
  - fixed tick simulation
  - one-zone world state
  - character persistence
        |
        v
SQLite now / Postgres later
```

## Movement Model

Movement is tile-stepped and server-authoritative. The world is a fixed tile grid, the client sends discrete step intents, and the server moves an entity exactly one tile per validated step, gated by a per-entity step cooldown. Movement is 8-way. The server rejects steps that target blocked or out-of-bounds tiles; entities do not block each other, so multiple players may share a tile.

Clients tween between confirmed tile centers over the step duration; there is no client prediction. Rationale: [networking-design-plan.md](networking-design-plan.md) section 5a.

## Projects

- `Mmo.Shared`: protocol messages, binary codec, and shared world/domain types.
- `Mmo.Server`: LiteNetLib server, simulation loop, session management, and SQLite/Postgres persistence.
- `Mmo.Client.Console`: diagnostic client for connection, movement, snapshots, and chat.
- `Mmo.Client.Web`: isometric Three.js browser debug client with a local WebSocket-to-LiteNetLib bridge.
- `Mmo.Shared.Tests`: focused protocol and domain tests.

## Server Responsibilities

- Accept or reject connections.
- Create sessions after login.
- Load or create persisted characters.
- Own player tile coordinates, facing, and movement cooldown.
- Evaluate snapshots at the configured tick rate and send changed state or heartbeat snapshots.
- Mark durable player tile changes dirty and queue write-behind persistence checkpoints.
- Flush queued character tile saves on disconnect and shutdown.

## Client Responsibilities

- Connect to the server with the shared connection key.
- Send login and player step-intent messages.
- Render or print server snapshots.
- Tween between confirmed tile centers.
- Never decide authoritative position.

## Object Separation Direction

Use the user-provided Albion Online separation diagram as a target shape for client/server boundaries:

```text
Server authority object
  - owns tile position and game rules
  - receives action requests
  - decides interest enter/leave

Client replicated object
  - mirrors server-approved state
  - sends action requests
  - does tile tweening locally

Client view object
  - owns renderer, animation, labels, effects, and local input affordances
  - can be destroyed or recreated without changing server state
```

The browser client already moved partway in this direction with `EntitySpawn` metadata plus compact snapshots. The next client cleanup should avoid mixing network state, tween state, and Three.js mesh/view state into one structure.

## Threading Model Direction

Use the Albion Online threading diagram as a target rule for server runtime safety:

```text
network/database/pathfinding/logging workers
        |
        | enqueue events or completed results
        v
single simulation thread
  - owns sessions and world state
  - applies validated input
  - runs ticks
  - schedules async work
  - emits outbound messages
```

The important constraint is ownership: game state should be mutated by one simulation thread. Worker threads can do blocking or expensive work, but they return results as events that the simulation thread polls and applies. This keeps correctness understandable before we introduce more entities, AI, pathfinding, or persistence work.

The current server already follows the early form of this model:

- LiteNetLib events are polled from the server loop.
- Login/database work runs asynchronously.
- Completed login work is returned through `_mainThreadActions`.
- The server loop owns movement, snapshots, and session mutation.
- Durable player tile writes are queued to a background persistence worker; periodic checkpoints
  enqueue dirty player tiles without doing database I/O on the simulation thread.

Next hardening steps:

- Wrap queued main-thread actions so one bad continuation cannot escape the loop.
- Replace raw `Action` continuations with typed server events when the queue grows.
- Keep database writes, pathfinding, and future AI jobs result-driven instead of letting worker threads mutate `ClientSession` or world entities directly.
- Add queue depth, job duration, and result latency metrics before adding more worker categories.

## Long-Term Server Farm Direction

Use the user-provided Albion Online server-farm diagram as a long-term pressure test, not a near-term implementation plan. The useful separation is:

- login/auth service
- front-door or gateway transport
- world/game server selected by world location
- supporting services for chat, marketplace, ranking/statistics, and back office tools
- separate persistence stores once domain ownership is clear

For this project, keep one process until metrics show a real bottleneck. The first split should probably be diagnostics/admin HTTP, then possibly login or gateway, and only later world-node routing.

## Future Pressure Points

- Interest management: replace O(n) radius selection with grid queries when metrics justify it.
- Protocol evolution: add feature flags and compatibility checks.
- Persistence: split online session state from durable character state.
- Godot: introduce tile tweening before considering any local-player prediction.
- Observability: add metrics endpoint or OpenTelemetry once the tick loop is stable.
- Persistence: keep SQLite and Postgres behind the same repository interface.
