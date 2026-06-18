# Networking Design Plan (Extrapolated from the References)

This document takes the depth-annotated [reference catalogue](networking-reference-catalogue.md) and extrapolates one best-fit plan for this project: server-authoritative, fixed 20 Hz, single-zone, slow 2D top-down "Ultima-like" sandbox in C#/.NET 8 on LiteNetLib, target roughly 120-150 visible players, SQLite-now/Postgres-later.

It is deliberately opinionated. Where the canon offers a menu, this picks one item and says why the others are wrong for this game. It is written to slot into [feature-roadmap.md](feature-roadmap.md), not to replace it.

## 1. Thesis

The best option is not a single clever library or algorithm. It is a boring, well-trodden architecture that the current code is already on the first step of:

> Server-authoritative state synchronization, sent as per-client interest-scoped snapshots over UDP, with reliable structural events and unreliable most-recent-state, smoothed by confirmed-state tweening/interpolation, evolving from full-state to delta-against-acked-baseline, and from naive radius AOI to a spatial grid, each step gated by measurement, inside one process until a profiled bottleneck forces a spatial split.

Three independent source families point in the same direction: MMO state sync, no determinism; object scoping and delivery classes; acked-baseline snapshot compression; client smoothing; eventual spatial split only after profiling. The scaling lever is not raw player count. It is how much authoritative state we keep and send to whom. Every optimization below is a way to send less, to fewer clients, less often, measured in order.

## 2. Prediction

No client prediction now. Probably not ever. Use confirmed-state smoothing instead.

- Prediction hides local input-to-feedback latency in twitch games. At walking speed in this sandbox, a short wait before movement starts is acceptable.
- Prediction costs input history, replay, correction, and snap-back behavior.
- Entity smoothing is cheap and mandatory. In protocol v9, tile tweening replaces the earlier remote snapshot interpolation buffer for movement.
- Lag compensation and server rewind are rejected. There is no hitscan or twitch combat.

If tile tweening is measured as laggy, revisit local-player-only prediction as a measured exception, not a default.

## 3. What To Adopt

Verdicts are tied to this project profile. NOW applies to the current single-zone prototype. SOON is the next natural step. LATER is correct technique gated on a measured trigger. NEVER is deliberately rejected for this genre/topology.

### ADOPT NOW

| # | Technique | Source(s) | Why now |
|---|---|---|---|
| N1 | Confirmed-state smoothing. For protocol v9 movement, use tile tweening after server confirmation; for any later non-tile state, use entity interpolation. | Gambetta, Source, VALORANT, Gaffer | Biggest perceived-quality win without prediction. |
| N2 | Snapshot sequence number plus client-to-server "last snapshot seq received" ack. | Gaffer, Quake 3, Source | Cheap hinge for later delta compression. |
| N3 | AOI is the anti-cheat boundary: entities outside a client's interest are never serialized into that client's packet. | VALORANT Fog of War | Prevents map/radar leakage and is testable. |
| N4 | Per-tick budget and drift metrics. Bucket `AOI / Serialize / Movement / Network / Persistence / Other`; track per-client bandwidth. | VALORANT 128-tick | Trigger mechanism for every later optimization. |
| N5 | Memory-authoritative world; DB is async write-behind. | Albion, Destiny | DB never gates the tick. |
| N6 | Single-source wire contracts in `Mmo.Shared`. | WoW/JAM, Albion | Prevents client/server serialization drift. |

### ADOPT SOON

| # | Technique | Source(s) | Why soon |
|---|---|---|---|
| S1 | Extract a data-oriented `WorldState` / `Zone`; entities as ids, not world state derived from live sessions. | Overwatch, VALORANT | Keystone item. Unblocks NPCs/items/non-session entities and isolates netcode-facing systems. |
| S2 | Transient vs durable-contract state split. | Destiny, Albion | Defines what snapshots carry versus what repositories store. |
| S3 | Delivery-class discipline on LiteNetLib channels. | Tribes, Source, LiteNetLib | Formalizes reliable structural events versus unreliable/sequenced state. |

### ADOPT LATER

| # | Technique | Source(s) | Trigger |
|---|---|---|---|
| D1 | Delta-against-acked-baseline snapshots plus per-client snapshot ring and dirty state masks. | Quake 3, Tribes, Source, Gaffer | When serialize/network buckets or per-client bandwidth grow. |
| D2 | Priority-accumulator packing instead of a hard visible-entity cap. | Gaffer, Tribes | When packet budget cannot include all important entities. |
| D3 | Grid/spatial-hash AOI. | Replication Graph, Tribes | When AOI bucket becomes a visible part of 50 ms tick budget. |
| D4 | Bitpacking the wire format. | mas-bandwidth, WoW/JAM | When Wireshark proves byte-granular encoding is the bandwidth cost. |
| D5 | Observer-gated simulation. | VALORANT | When idle-entity simulation shows up in tick budget. |
| D6 | Datablock pattern for static templates. | Tribes | When static metadata repetition becomes measurable. |

### ADOPT MUCH LATER

| # | Technique | Source(s) | Note |
|---|---|---|---|
| L1 | Spatial region split with entity hand-off at boundaries. | Ultima Online, GoWorld | Prep seams now; build only when one process is profiled as the bottleneck. |
| L2 | Entity-addressed routing seam. | WoW/JAM | Direct in-process call today, routing later. |
| L3 | Out-of-band service stack: login/accounts, admin RPC, fleet, edge UDP proxy. | netcode, MagicOnion, Nakama, Agones, Quilkin | First likely split is diagnostics/admin/login, not sharding. |

### NEVER

- Client-side prediction by default, reconciliation, lag compensation, server rewind.
- Deterministic lockstep, input-sync, P2P.
- Rollback netcode.
- Raycast/PVS line-of-sight for AOI.
- Extrapolation/dead reckoning.
- Hand-rolled UDP reliability/congestion control.
- Engine-coupled or SaaS netcode such as Mirror, FishNet, Netick, Photon, Colyseus, Normcore.

## 4. Sequenced Path

1. Robustness first: crash-proof tick/snapshot paths, movement validation, network-id recycling.
2. Observability: per-tick budget, drift profiling, per-client bandwidth.
3. Movement model: protocol v9 tile-stepped movement, server-timed cooldown, blocked-tile map, confirmed tile tween.
4. Cheap hinge/security: snapshot sequence ack and AOI-as-anti-cheat tests.
5. Persistence boundary and wire contracts: async write-behind and `Mmo.Shared`.
6. WorldState/Zone extraction: data-oriented world, transient/durable split, delivery classes.
7. Measured optimization: delta snapshots, bitpacking, grid AOI, priority packing only when metrics demand them.
8. Spatial split only if profiled as necessary.

## 5. Tooling

- Now on Windows: clumsy for real-socket latency/loss/reorder/dup, Wireshark for wire sizes, and LiteNetLib's built-in latency/loss simulation for fast inner-loop checks.
- Load: headless bot clients at 150+, with bad-connection subsets; watch tick budget and per-client bandwidth.
- Later on Linux/CI: netem for scriptable conditioning; mitmproxy only for HTTP login/web side.

## 5a. Movement Model: Tile-Stepped (Implemented)

The original prototype streamed continuous float positions every tick. Protocol v9 replaced that with tile-stepped movement.

The world is a fixed W x H tile grid. The client sends a discrete step intent. The server validates cooldown, bounds, and blocked tiles, moves the entity exactly one tile if legal, and records facing. Snapshots carry integer tile coordinates plus facing. The client tweens the mesh from the old tile center to the new tile center over the step duration after server confirmation.

Locked decisions:

- Server-timed steps plus client tween.
- Default walk cooldown is 200 ms, configurable through `MMO_STEP_COOLDOWN_MS`.
- 8-way movement: `N`, `NE`, `E`, `SE`, `S`, `SW`, `W`, `NW`.
- Real blocked-tile map from the start.
- Walls block movement; entities do not block each other.
- Diagonal corner-cutting is ignored for v1 and should be revisited only when collision data gets richer.

This removes continuous movement streaming, gives collision/pathfinding a tile substrate, and keeps the no-prediction decision intact. Out of scope for this pass: client prediction, pathfinding, and LOS.

## 6. One-Line Summary

Keep doing exactly what the architecture already does: authoritative state-sync snapshots over LiteNetLib, growing along the Tribes to Quake 3 to Replication Graph axis (object scoping, acked-baseline deltas, grid AOI), smoothed by confirmed-state tweening/interpolation, measured by a tick budget, persisted memory-first/write-behind, and split spatially like Ultima Online only when a profiler says so.
