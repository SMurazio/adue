# Feature Roadmap

This roadmap is intentionally phased. The project is still building the MMO spine, so the next work should make the single-zone prototype stable and observable before adding larger gameplay systems.

## Near-Term Queue

These are the next practical tasks before new gameplay:

1. Add startup validation for `ServerOptions`: port range, tick rate, movement speed, interest radius, and visible entity cap should fail fast with clear errors.
2. Wrap queued main-thread actions so one unexpected login/send/session exception cannot escape the server loop.
3. Harden bad-packet handling: count bad packets per session, avoid echoing raw exception details, and disconnect after a small threshold.
4. Add integration coverage for AOI enter/leave behavior now that `EntitySpawn`, `WorldSnapshot`, and `EntityDespawn` are separate messages.
5. Improve stress-client reporting: connect timeout, login timeout, minimum auth rate, max error rate, and JSON/CSV output for run comparisons.
6. Add latency percentiles to stress reports instead of relying only on average/max.
7. Extract a small `WorldState`/`Zone` model so world entities stop being derived entirely from live sessions.
8. Add a lightweight diagnostics endpoint after the in-game metrics stabilize.

## Phase 0: Finish Admin And Debug Work

- Finish role propagation end to end.
- Keep admin commands explicit and bounded: `/help`, `/role`, `/who`, `/metrics`, `/stress`, `/stress status`, `/stress start`, `/stress stop`.
- Keep synthetic clients capped by count and duration.
- Add tests around role serialization and command parsing if command parsing is factored out.

Do not turn admin commands into a general scripting or shell system.

## Phase 1: Stabilize Server Runtime

- Separate connection/session lifecycle, login, command handling, world simulation, and snapshot broadcast.
- Add an explicit world model instead of deriving every entity directly from connected sessions.
- Track tick duration, drift, skipped ticks, snapshot count, peer count, and authenticated player count.
- Add basic validation: unauthenticated action rejection, movement vector clamping, chat/command rate limits, and clear bad-packet handling.
- Keep send paths defensive so one oversized or invalid packet cannot escape the main loop.

Avoid sharding, gateways, actor systems, or multi-zone architecture until the single-zone server is boring.

## Phase 2: Networking And Protocol Maturity

- Document delivery rules per message type.
- Use movement sequence numbers to reject stale input.
- Add protocol mismatch handling and reserve message IDs for future growth.
- Add counters for messages and bytes in/out by message type.
- Add local artificial latency/loss settings for testing interpolation and responsiveness.
- Keep the near-term channel target explicit: 120-150 connected clients per channel.
- Keep snapshots below the UDP packet budget; do not resend static identity data in every movement tick.
- Keep reliable spawn metadata separate from unreliable high-frequency entity state.
- Recycle channel-local network ids before long-running churn can exhaust the compact snapshot id range.
- Add delta snapshots only after full packed snapshots are well measured.

Do not over-invest in client prediction until interpolation, tick timing, and server authority are stable.

## Phase 3: Persistence Foundations

- Keep SQLite as the default.
- Add repository tests with temporary SQLite databases.
- Test clean database bootstrap and existing database migration.
- Persist character identity, display name, zone id, and position reliably.
- Revisit Postgres only after the SQLite path is proven.

Avoid account security, inventory schemas, item databases, and character creation complexity until login/session/persistence is stable.

## Phase 4: Gameplay Foundations

- Introduce `WorldState` and `Zone` abstractions.
- Introduce explicit server-side entity objects instead of deriving world state directly from sessions.
- Add world bounds and movement clamping.
- Add basic non-player entity kinds only when needed: NPC placeholder, static object, resource node.
- Add server-validated interactions: target entity, validate distance, emit result.
- Add local/system/admin chat channels.
- Add spawn points plus admin teleport/summon commands.

Combat should wait until movement, snapshots, targeting, and persistence are reliable.

## Phase 5: Admin And Observability Tooling

- Add admin commands around debugging: `/teleport`, `/summon`, `/kick`, `/setpos`, `/metrics`, `/entities`.
- Add structured log lines or consistent key-value logs.
- Add an optional HTTP diagnostics endpoint for health, peer count, tick stats, message counters, and synthetic load status.
- Improve the browser debug client with selected entity details, latency/status, and a dedicated command input.

Keep this as debug tooling, not the final game UI.

## Phase 6: Testing And Load

- Add integration tests for two-client login, movement snapshots, chat broadcast, reconnect persistence, non-admin command denial, and admin command success.
- Add deterministic stress profiles: 10-client smoke, 100-client local baseline, chat-heavy run, and connect/disconnect churn.
- Track regression numbers manually at first: max stable clients, average latency, snapshot bandwidth, and tick duration.

Load testing without metrics is mostly guesswork; add counters before chasing performance.

## Phase 7: Interest Management

- Continue hardening radius-based area-of-interest filtering.
- Move to a grid or spatial hash after entity counts make naive per-client distance checks measurable.
- Preserve packet-budgeted snapshots: include self first, then use stable AOI priority when channel population exceeds the visible target.
- Test entities entering and leaving visibility.
- Measure bandwidth before and after.
- Target 120-150 visible players as a deliberate stress case, not as an excuse to hide entities prematurely.

Do not add multi-zone or map instances until interest management works in one zone.

## Phase 8: Godot Client Direction

- Keep Godot as a client only; keep server authority in .NET.
- Build a thin Godot networking layer that matches the shared protocol.
- First milestone: connect, login, render snapshots, interpolate movement, send movement input, and show chat.
- Use the browser debug client as a behavior reference.
- Follow the server object, replicated client object, view object separation before building real UI or combat presentation.

Start Godot after the protocol and server runtime stop changing every session.

## Phase 9: Multi-Process Architecture Study

- Study front-end transport, simulation node, routing/cluster, login, persistence, and process manager boundaries.
- Study Albion-style service boundaries: login, chat, world/game, market, statistics/ranking, back office, and independent databases.
- Keep a written trigger for each split: measured CPU, bandwidth, fault isolation, deployment workflow, or ownership clarity.
- First likely split is an HTTP diagnostics/admin surface, not sharding.
- Later splits may look like gateway/front-end, world node, login service, persistence worker, and local supervisor.

Do not implement a cluster until the one-process server can explain its own bottlenecks with metrics.
