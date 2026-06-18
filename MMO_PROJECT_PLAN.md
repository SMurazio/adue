# MMO Project LLM Handoff Spec

## Goal

Build a production-shaped 2D top-down MMO learning project from scratch. Optimize for understanding MMO server architecture, networking, persistence, observability, and client/server boundaries.

The project should remain small enough to reason about, but the structure should resemble real server software: clear domain types, authoritative simulation, explicit protocol messages, database migrations, local deployment, and tests.

## Current Decisions

- Server language: C# on .NET 8.
- Transport: LiteNetLib reliable UDP.
- Persistence: SQLite first, with a Postgres migration path kept for later.
- Game shape: one-zone 2D top-down Ultima-like sandbox.
- Authority: server owns truth; clients send commands.
- Simulation: fixed tick loop.
- Movement model: tile-stepped on a tile grid, implemented in protocol v9. The server times steps with a per-entity cooldown, supports 8-way movement, validates blocked tiles, and clients tween between confirmed tiles. This replaces continuous streamed positions. See [networking-design-plan.md](docs/networking-design-plan.md) section 5a.
- First client: diagnostic console client.
- Later client: Godot, using the same protocol.

## Milestone 1: Foundation

Deliverables:

- Git repo with `.NET` solution and shared/server/client/test projects.
- SQLite local database file.
- Optional Docker Compose Postgres service for later.
- Migration runner in the server.
- Shared protocol/domain library.
- Project docs and runbook.

Acceptance:

- `dotnet restore`, `dotnet build`, and `dotnet test` work on a machine with .NET 8 SDK.
- Optional: `docker compose up -d db` starts local Postgres after Docker is available.
- The server can create `data/mmo.db` and apply SQLite migrations.

## Milestone 2: Networked One-Zone Sandbox

Deliverables:

- LiteNetLib server listening on UDP.
- Console client can connect, login, move, and chat.
- Server creates or loads one persisted character per account/display name.
- Server evaluates snapshots on a fixed tick and sends changed state or heartbeat snapshots.
- Two clients can see each other in the same zone.

Acceptance:

- Start server and two clients locally.
- Client A sees Client B in snapshots.
- Chat from Client A appears on Client B.
- Movement is accepted only as step input; server computes actual tile positions.
- Disconnect/reconnect persists the latest tile position.

## Milestone 3: Production Learning Layer

Deliverables:

- Packet counters, tick timing, connected peer count, and latency logging.
- Packet loss/latency simulation settings.
- Integration tests for two-client behavior.
- Runbook entries for common failures.

Acceptance:

- Server logs enough information to debug connection/session/tick problems.
- Tests cover protocol round trips and persistence behavior.
- Local runbook can recover from a bad database state.

## Non-Goals For Now

- No sharding.
- No combat yet.
- No Godot client yet.
- No account security beyond a dev login identifier.
- No real asset pipeline.
- No cloud deployment until the local spine works.

## Reference Repo

Study `godot-tiny-mmo` for Godot-side structure, gateway/master/world separation, AOI, map instances, and interpolation ideas. Do not use it as the base repo; this project keeps a standalone .NET server and treats Godot as a later client.

## External References To Study

- Curated project list: `docs/networking-references.md`
- https://wirepair.org/2023/06/29/so-you-want-to-build-an-mmorpg-server/
- https://github.com/0xFA11/MultiplayerNetworkingResources
- https://www.reddit.com/r/gamedev/comments/1w746u/interested_in_mmo_server_architecture/
- User-provided Albion Online architecture slides: object separation and server-farm service split.

Use these as architecture pressure tests, not as a mandate to split the prototype too early. The useful long-term pattern is separation of transport/front door, simulation ownership, routing, persistence, and process supervision. The current prototype keeps those concerns in one process until the single-zone runtime is stable and measured.

The Albion-style object separation is useful much sooner than the server farm. Aim for server authority objects, client replicated objects, and client view objects as separate concepts before adding richer gameplay.

## Database Path

Use SQLite during the locked-down-machine phase. Keep database calls behind `ICharacterRepository` so Postgres can replace SQLite later without changing networking, simulation, or client code.

## Agent Workflow Skill

Maintain repo-local skill `.codex/skills/mmo-dev/SKILL.md` for repeatable development workflows. Add scripts only for actions that are frequent, stateful, or easy to run incorrectly.

## Debug Client Direction

Use the browser debug client as the primary manual test surface. Keep it basic but visual: isometric Three.js scene, tile grid, blocked walls, 8-way movement, entity list, chat, metrics, and connection status. Do not let it become the production game client yet.

## Implementation Defaults

- Keep the protocol binary and versioned.
- Use reliable ordered delivery for login/chat/entity spawn metadata.
- Use unreliable packet-budgeted delivery for compact snapshots.
- Keep hot snapshots small: channel-local network id plus tile coordinate/facing, not repeated names/kinds/character ids.
- Use a single zone with radius-based area-of-interest; AOI is also the anti-cheat boundary, so entities outside a client's interest are never serialized to it.
- Movement is tile-stepped: discrete server-validated steps gated by a step cooldown, not continuous streamed positions. Walls block; entities may share a tile.
- Prefer boring SQL over an ORM until the persistence model earns more complexity.
- Keep the server loop single-process and single-zone until profiling shows a real need to split it.
