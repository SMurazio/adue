# Feature Roadmap

This roadmap is intentionally phased. The project is still building the MMO spine, so the next work should make the single-zone prototype stable and observable before adding larger gameplay systems.

The networking rationale is [Networking Design Plan](networking-design-plan.md), backed by the complete [Networking Reference Catalogue](networking-reference-catalogue.md). The design axis is authoritative state-sync snapshots with AOI scoping and confirmed-state smoothing now, then measured deltas/grid AOI later. The explicit resolution against older planning language is: no client prediction, reconciliation, lag compensation, lockstep, rollback, P2P, raycast/PVS visibility, extrapolation, hand-rolled UDP reliability, or engine/SaaS netcode in the current MMO spine.

Movement model (implemented in protocol v9): **tile-stepped** on a tile grid - server-timed steps with a per-entity cooldown, 8-way, blocked-tile map (walls block; entities may share a tile), client tweens between tiles, no prediction. This replaced continuous streamed positions. See [Networking Design Plan](networking-design-plan.md) section 5a.

## Status as of 2026-06-19 (what's shipped vs. what the phases below still describe as future)

Several items below were written before they shipped. Already done — read the phases as history for these:

- **`WorldState`/`Zone` extraction** (Near-Term #10, Phase 4 first bullet): done. Entities are no longer
  derived from sessions; `Zone` owns the tile grid. See `worldstate-zone-design.md` (now a historical
  record).
- **Non-player entities + resource nodes** (Phase 4): done — `EntityKind.Resource`, server-owned,
  transient (S38).
- **Inventory / items** (Phase 3 caveat, Phase 4): done — item registry + per-character inventory with
  write-behind SQLite persistence (S37).
- **Server-validated interactions** (Phase 4): done — the `Interact`/harvest verb with auth + AOI +
  adjacency validation and owner-only `InventoryUpdate` (S38).
- **Wire version**: now **v13** (S38), going to **v14** with terrain chunking (S36a) — not the "v9"
  some older lines still cite (v9 was when tile-stepping landed; the model is unchanged, the wire moved on).
- **Channel cap (the "120–150" target, Phase 2 & 7)**: measured in `capacity-ladder-study.md` (S40) —
  server CPU is not the bound (≈2 ms of a 50 ms budget at 150 *visible*); per-client bandwidth + the AOI
  scan at high *visible* density are. 120–150 is a conservative floor; raise it after S36a + grid AOI (S41).

Current active priority is the **optimization/scaling track** (S36a → S41 → S36b) ahead of gameplay UI
(S39) and feel-polish (N21/S28) — see `todo/`.

## Near-Term Queue

These are the next practical tasks before new gameplay:

1. Keep robustness closed: startup validation, crash-proof tick/snapshot paths, tile-step validation, bad-packet disconnects, and network-id recycling must stay covered by regression tests.
2. Keep per-tick budget profiling and drift metrics useful, bucketed into movement, AOI, serialization, network, persistence, and other work. This is the trigger mechanism for every later optimization.
3. Keep per-client bandwidth counters visible so stress runs show who is receiving how much snapshot/event traffic.
4. Keep snapshot sequence numbers and client-to-server last-snapshot-sequence ack wired. The ack exists to unlock later delta compression without redesign.
5. Enforce AOI as an anti-cheat invariant: outside a client's AOI means never serialized into that client's packet, covered by integration tests.
6. Keep the browser debug client on confirmed tile tweening. Do not add prediction.
7. Keep persistence memory-first and write-behind: no database read/write in the tick hot path except explicit async boundaries.
8. Formalize LiteNetLib delivery classes and channels: reliable ordered structural events, unreliable/sequenced state, and documented message ownership in `Mmo.Shared`.
9. Improve stress-client reporting: connect timeout, login timeout, minimum auth rate, max error rate, JSON/CSV output, and latency percentiles for run comparisons.
10. Extract a small data-oriented `WorldState`/`Zone` model so entities stop being derived entirely from live sessions.
11. Add a lightweight diagnostics endpoint after the in-game metrics stabilize.

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
- Track per-tick category costs and per-client bandwidth; use these numbers as the gate for delta snapshots, grid AOI, and any process split.
- Add basic validation: unauthenticated action rejection, tile-step cooldown/walkability checks, chat/command rate limits, and clear bad-packet handling.
- Keep send paths defensive so one oversized or invalid packet cannot escape the main loop.

Avoid sharding, gateways, actor systems, or multi-zone architecture until the single-zone server is boring. See [Networking Design Plan](networking-design-plan.md) sections 3-4 for why metrics come before optimization.

## Phase 2: Networking And Protocol Maturity

- Document delivery rules per message type.
- Use movement sequence numbers to reject stale input.
- Keep snapshot sequence numbers and client snapshot acks wired while full snapshots remain the baseline.
- Treat AOI filtering as a security boundary, not just a rendering optimization.
- Add protocol mismatch handling and reserve message IDs for future growth.
- Add counters for messages and bytes in/out by message type.
- Add local artificial latency/loss settings for testing smoothing and responsiveness.
- Keep the near-term channel target explicit: 120-150 connected clients per channel.
- Keep snapshots below the UDP packet budget; do not resend static identity data in every movement tick.
- Keep reliable spawn metadata separate from unreliable state.
- Recycle channel-local network ids before long-running churn can exhaust the compact snapshot id range.
- Add delta snapshots only after full packed snapshots are well measured and snapshot ack baselines exist.

Do not add client prediction, reconciliation, lag compensation, extrapolation, or rewind. For this slow top-down MMO spine, confirmed-state tile tweening is the chosen model; revisit local-player-only prediction only if measured movement latency proves unacceptable.

## Phase 3: Persistence Foundations

- Keep SQLite as the default.
- Add repository tests with temporary SQLite databases.
- Test clean database bootstrap and existing database migration.
- Persist character identity, display name, zone id, and tile coordinates reliably.
- Keep durable state separate from transient state; tile coordinates are server-memory truth with checkpoint/write-behind persistence.
- Revisit Postgres only after the SQLite path is proven.

Avoid account security, inventory schemas, item databases, and character creation complexity until login/session/persistence is stable.

## Phase 4: Gameplay Foundations

- Introduce `WorldState` and `Zone` abstractions; the `Zone` owns the tile grid and blocked-tile map.
- Introduce explicit server-side entity objects instead of deriving world state directly from sessions.
- Classify state as transient/lossy versus durable-contract before adding complex entity types.
- Keep tile-stepped movement stable; later expand the blocked-tile data model for richer collision/pathfinding only when gameplay requires it.
- Add basic non-player entity kinds only when needed: NPC placeholder, static object, resource node.
- Add server-validated interactions: target entity, validate distance, emit result.
- Add local/system/admin chat channels.
- Add spawn points plus admin teleport/summon commands.

Combat should wait until movement, snapshots, targeting, and persistence are reliable.

## Phase 5: Admin And Observability Tooling

- Add admin commands around debugging: `/teleport`, `/summon`, `/kick`, `/setpos`, `/metrics`, `/entities`.
- Add structured log lines or consistent key-value logs.
- Add an optional HTTP diagnostics endpoint for health, peer count, tick stats, message counters, and synthetic load status.
- Improve the browser debug client with tile/tween diagnostics, selected entity details, latency/status, and a dedicated command input.

Keep this as debug tooling, not the final game UI.

## Phase 6: Testing And Load

- Add integration tests for two-client login, movement snapshots, chat broadcast, reconnect persistence, non-admin command denial, and admin command success.
- Add deterministic stress profiles: 10-client smoke, 100-client local baseline, chat-heavy run, and connect/disconnect churn.
- Track regression numbers manually at first: max stable clients, average latency, snapshot bandwidth, and tick duration.
- Add stress profiles that report idle versus 120-150 visible-player tick-budget buckets.

Load testing without metrics is mostly guesswork; add counters before chasing performance.

## Phase 7: Interest Management

- Continue hardening radius-based area-of-interest filtering.
- Move to a grid or spatial hash after entity counts make naive per-client distance checks measurable.
- Use AOI bucket measurements as the trigger for grid/spatial-hash work.
- Preserve packet-budgeted snapshots: include self first, then use stable AOI priority when channel population exceeds the visible target.
- Test entities entering and leaving visibility.
- Measure bandwidth before and after.
- Target 120-150 visible players as a deliberate stress case, not as an excuse to hide entities prematurely.

Do not add multi-zone or map instances until interest management works in one zone.

## Phase 8: Godot Client Direction

- Keep Godot as a client only; keep server authority in .NET.
- Build a thin Godot networking layer that matches the shared protocol.
- First milestone: connect, login, render snapshots, tween movement, send movement input, and show chat.
- Use the browser debug client as a behavior reference.
- Follow the server object, replicated client object, view object separation before building real UI or combat presentation.
- Do not port engine-coupled networking stacks; Godot consumes the shared protocol/client model.

Start Godot after the protocol and server runtime stop changing every session.

## Phase 9: Multi-Process Architecture Study

- Study front-end transport, simulation node, routing/cluster, login, persistence, and process manager boundaries.
- Study Albion-style service boundaries: login, chat, world/game, market, statistics/ranking, back office, and independent databases.
- Keep a written trigger for each split: measured CPU, bandwidth, fault isolation, deployment workflow, or ownership clarity.
- First likely split is an HTTP diagnostics/admin surface, not sharding.
- Later splits may look like gateway/front-end, world node, login service, persistence worker, and local supervisor.

Do not implement a cluster until the one-process server can explain its own bottlenecks with metrics.
