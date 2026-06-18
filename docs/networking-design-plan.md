# Networking Design Plan (Extrapolated from the References)

This document does the second half of the task: it takes the depth-annotated
[reference catalogue](networking-reference-catalogue.md) and extrapolates **one best-fit plan**
for *this* project — a server-authoritative, fixed-20 Hz, single-zone, slow 2D top-down
"Ultima-like" sandbox in C#/.NET 8 on LiteNetLib, target ~120-150 visible players,
SQLite-now/Postgres-later.

It is deliberately opinionated. Where the canon offers a menu, this picks one item and says why
the others are wrong *for this game*. It is written to slot into the existing
[feature-roadmap.md](feature-roadmap.md), not to replace it.

---

## 1. The thesis the references converge on

Read across ~150 resources, the "best option" is not a single clever library or algorithm. It is
a **boring, well-trodden architecture that the current code is already on the first step of**:

> **Server-authoritative state synchronization, sent as per-client interest-scoped snapshots over
> UDP, with reliable structural events and unreliable most-recent-state, smoothed by client-side
> interpolation — evolving from full-state → delta-against-acked-baseline, and from naive radius
> AOI → a spatial grid, each step gated by a measurement, inside one process until a profiled
> bottleneck forces a spatial split.**

Three independent sources (0fps, Ruoyu Sun, Valve) say *MMO ⇒ state sync, no determinism*. Tribes
gives the exact object model. Quake 3 / Source give the exact compression path. Gambetta / VALORANT
give the client smoothing. UO gives the eventual split shape. Albion gives the persistence shape.
They agree more than they disagree, and the agreement points at a single coherent design.

**The single most important meta-lesson (VALORANT 128-tick + the whole roadmap ethos): the scaling
lever is not player count, it is *how much state you keep authoritative and send to whom*. Every
optimization below is a way to send less, to fewer clients, less often — measured, in order.**

## 2. The prediction question, answered

The previous planning attempt likely got lost here, because most of the famous references
(Gambetta, Source, Overwatch, VALORANT) spend their energy on **client-side prediction +
reconciliation + lag compensation**. For this game the answer is explicit:

**No client prediction now. Probably not ever. Use entity interpolation instead.**

- Prediction exists to hide *local-player input→feedback latency* in **twitch** games. At walking
  speed in a top-down sandbox, 50-100 ms before your own avatar starts moving is barely
  perceptible. AoE's own data: <250 ms is unnoticed.
- Prediction costs a lot: input sequence numbers, a pending-input buffer, replay-on-correction,
  and a whole class of mispredict/snap-back bugs — directly against the project's "avoid premature
  complexity" rule.
- **Entity interpolation is cheap, mandatory, and currently missing.** Without it, 20 Hz snapshots
  make every remote entity teleport every 50 ms.
- Lag compensation / server rewind: **reject** — there is no hitscan; slow interactions resolve
  authoritatively server-side.

If, after interpolation ships and is measured, local movement still feels laggy, *then* add
prediction **for the local player only** — as a measured decision, not a default.

## 3. What to adopt, and when

Verdicts are tied to the project profile. "NOW" = applies to the current single-zone prototype.
"SOON" = the next natural step. "LATER" = correct technique, gated on a measured trigger.
"NEVER" = deliberately rejected for this genre/topology.

### ADOPT NOW (cheap, high-leverage, no new bottleneck required)

| # | Technique | Source(s) | Why now |
|---|---|---|---|
| N1 | **Client-side entity interpolation**: render remote entities ~100 ms (2-3 ticks) in the past; buffer ~150 ms / 3 snapshots; linear interp (2D, slow). | Gambetta, Source, VALORANT, Gaffer (interp) | The biggest perceived-quality win; the missing half of the snapshot model. Implement in the Three.js client; carry the same model to Godot. |
| N2 | **Snapshot sequence number + client→server "last snapshot seq received" ack.** Harmless under full-state today. | Gaffer (reliability), Quake 3, Source | The cheap *hinge*: a single field that later unlocks delta compression (D-LATER) with zero redesign. Add it before you need it. |
| N3 | **AOI is the anti-cheat boundary**: entities outside a client's interest are *never serialized into that client's packet* — not sent-then-hidden. | VALORANT Fog of War | A top-down world is very exposed to map/radar hacks. Make "not in AOI ⇒ absent from packet" an enforced invariant. (The code already filters; make it a tested rule.) |
| N4 | **Per-tick budget (50 ms) + category profiling + drift metrics.** Bucket the tick into `AOI / Serialize / Movement / Network / Persistence / Other`; log per-tick ms and schedule drift. | VALORANT 128-tick | This is roadmap "tick-timing/drift metrics." It is also the *trigger mechanism* for every LATER item — you migrate AOI/compression when a bucket actually grows, not on a hunch. |
| N5 | **Memory-authoritative world; DB is async write-behind.** No DB reads in the tick loop; persistence is batched/checkpointed off the hot path. | Albion, Destiny (state split) | The world already lives in memory; make the persistence boundary explicitly async so the DB never gates the tick and a later DB split touches no gameplay code. |
| N6 | **Single-source wire contracts** shared between server and (future Godot) client; hand-write pack/unpack in one place, or generate it. | WoW/JAM, Albion | Kills a whole bug class (client/server serialization drift). The `Mmo.Shared` project is already the right home — keep both ends on it. |

### ADOPT SOON (the next structural step — roadmap's WorldState/Zone item)

| # | Technique | Source(s) | Why soon |
|---|---|---|---|
| S1 | **Extract a data-oriented `WorldState` / `Zone`** with structure-of-arrays component storage (`Position[]`, `Velocity[]`, …), entities as ids — *not* world state derived from live sessions. | Overwatch (ECS), VALORANT (cache locality) | Roadmap's keystone item. Unblocks NPCs/items/non-session entities, gives a cache-friendly tick loop, and lets you **quarantine the netcode surface** to ~2 systems (movement, AOI/replication). |
| S2 | **Transient vs durable-contract state split**: classify every piece of state as in-memory/lossy (positions, mob wander) or persisted/authoritative (identity, position checkpoints, container contents, ownership, flags). | Destiny ("Activity State"), Albion | Defines exactly what the snapshot carries vs what the repository stores. Keeps persistence light and makes reconnection trivial (state sync ⇒ rejoin = receive a fresh snapshot). |
| S3 | **Delivery-class discipline on LiteNetLib channels**: structural events (spawn/despawn/inventory/chat) on Reliable Ordered; high-frequency state on a *separate* Unreliable/Sequenced channel so a big reliable payload never head-of-line-blocks movement. | Tribes (delivery classes), Source, LiteNetLib | The code already splits reliable-spawn / unreliable-snapshot; formalize it as named channels with a documented per-message delivery class. |

### ADOPT LATER (correct technique; wait for the measured trigger)

| # | Technique | Source(s) | Trigger |
|---|---|---|---|
| D1 | **Delta-against-acked-baseline snapshots** + per-client snapshot ring + per-entity dirty **state masks** (most-recent-state wins; self-healing under loss, no retransmit). | Quake 3, Tribes, Source, Gaffer (compression) | When the `Serialize`/`Network` tick buckets or per-client bandwidth grow under load. N2's snapshot-ack is the prerequisite. |
| D2 | **Priority-accumulator packing** replaces the hard visible-entity cap: per-entity priority (distance, player-vs-NPC, recently-changed, interest class) accumulates, sort, fill the packet budget, reset on send. | Gaffer (state sync), Tribes (priority) | When the flat `MaxVisibleEntities` cap starts dropping entities that matter, or when one packet can't hold everyone in radius. |
| D3 | **Grid / spatial-hash AOI**: bucket entities into cells (cell ≈ interest radius); per-client gather = own cell + 8 neighbors (list union, not O(n²) distance). Move entities between cells only on boundary crossing. | Replication Graph, Tribes (scoping) | When the `AOI` tick bucket becomes a visible fraction of 50 ms (entity count inflates well past player count once NPCs/items/projectiles exist). |
| D4 | **Bitpacking** the wire format (values in minimum bits, not whole bytes); one serialize function for read/write/measure. | mas-bandwidth `serialize`, WoW/JAM | When packet sizes (verified in Wireshark) show byte-granular encoding is the bandwidth cost. Pairs with D1. |
| D5 | **Observer-gated simulation**: entities with no client in their AOI cell tick at reduced rate / skip work. | VALORANT (don't simulate the unobserved) | When idle-entity simulation shows up in the tick budget at scale. |
| D6 | **Datablock pattern**: static templates (item/tile/NPC definitions) sent once at join, referenced by id in snapshots. | Tribes (datablocks) | When the entity catalogue grows enough that repeating static metadata on the wire is measurable. |

### ADOPT MUCH LATER (the eventual process split — prep the seams now, build nothing)

| # | Technique | Source(s) | Note |
|---|---|---|---|
| L1 | **Spatial region split with entity hand-off at boundaries** (UO "server lines"): partition the map into regions, one process per region, serialize+transfer entity ownership at seams, pre-warm adjacent-region ghosts. | Ultima Online, GoWorld | The genre-correct split. *Prep now* (clean entity serialization, no cross-region in-memory pointers); *build only* when one process is profiled as the bottleneck. |
| L2 | **Entity-addressed routing seam**: gameplay says "send to entity N" through a thin dispatch indirection — a direct in-process call today, inter-process routing later. | WoW/JAM | The mechanism that makes L1 cheap to introduce. Stub the indirection now as a no-op call; gameplay code never learns about the topology. |
| L3 | **Out-of-band service stack**: login/accounts (connect tokens), matchmaking, admin RPC, fleet, edge UDP proxy. | netcode (tokens), MagicOnion, Nakama, Agones, Quilkin | First likely split is an HTTP diagnostics/admin + login surface, not sharding — matches the roadmap. Connect-token auth (libsodium-signed, short-lived) is the pattern to reimplement minimally when auth arrives. |

### NEVER (deliberately rejected for this game)

- **Client-side prediction by default, reconciliation, lag compensation / server rewind** — twitch
  concerns; slow top-down movement doesn't need them (revisit prediction only if measured as
  laggy, local-player only).
- **Deterministic lockstep / input-sync / P2P** — wrong topology, FP-desync-prone, n² bandwidth,
  and *unnecessary*: state sync needs no determinism (0fps, Ruoyu Sun).
- **Rollback netcode** — fighting-game/2-player technique.
- **Raycast line-of-sight / PVS** — a radius/grid AOI is the fog-of-war for top-down; add cheap
  per-room relevance only if walls/dungeons later need occlusion.
- **Extrapolation / dead-reckoning** — interpolation is correct for unpredictable slow movement.
- **Hand-rolled UDP reliability/congestion control** — LiteNetLib owns that; borrow ideas from
  `reliable`/`netcode`, don't reimplement the transport.
- **Engine-coupled or SaaS netcode (Mirror/FishNet/Netick/Photon/Colyseus/Normcore)** — conflicts
  with the standalone, self-built, learning goal; useful only as design references.

## 4. The sequenced path (how this lands in the roadmap)

Ordered so each step is independently shippable and de-risks the next. Maps onto the existing
[feature-roadmap.md](feature-roadmap.md) phases rather than introducing a parallel plan.

1. **Robustness first (already flagged):** crash-proof the tick, movement bounds/clamping, network-id
   recycling. *Nothing below matters if a wandering player can crash the server.*
2. **Observability (N4):** per-tick budget + category/drift profiling + per-client bandwidth
   counters. This is the instrument that triggers every LATER step. Do it before optimizing
   anything.
3. **Client smoothing (N1):** entity interpolation in the Three.js client (~150 ms buffer). Biggest
   visible quality jump; zero server risk.
4. **The cheap hinge (N2, N3):** add a snapshot sequence number + client snapshot-ack, and enforce
   the AOI-as-anti-cheat invariant with a test. Both are small and unlock/secure later work.
5. **Persistence boundary (N5) + wire contracts (N6):** make persistence explicitly async
   write-behind; keep both ends on `Mmo.Shared`.
6. **WorldState/Zone extraction (S1, S2, S3):** the structural keystone — data-oriented world,
   transient/durable state split, formalized delivery-class channels. Unblocks gameplay.
7. **Measured optimization (D1-D6), in whatever order the profiler demands:** typically delta
   snapshots (D1) + bitpacking (D4) when bandwidth bites, grid AOI (D3) when the AOI bucket grows,
   priority packing (D2) when the cap drops entities that matter.
8. **Only then, if profiled as necessary, the spatial split (L1-L3)** — with the seams (L2)
   already stubbed so it's cheap.

## 5. Tooling

- **Now (Windows):** [clumsy](https://jagt.github.io/clumsy/) to inject real-socket
  latency/loss/reorder/dup (beyond LiteNetLib's in-process simulator), and
  [Wireshark](https://www.wireshark.org/) to verify wire sizes, sub-MTU snapshots, and that any
  bitpacking actually pays off. Use LiteNetLib's built-in `SimulateLatency`/`SimulatePacketLoss`
  for fast inner-loop checks.
- **Load:** headless bot clients (the existing stress tool) at 150+, with clumsy degrading a
  subset to model bad connections; watch the tick budget and per-client bandwidth from step 2.
- **Later (Linux deploy/CI):** [netem](https://wiki.linuxfoundation.org/networking/netem) for
  scriptable conditioning; [mitmproxy](https://mitmproxy.org/) only for the HTTP login/web side.

## 6. One-line summary

Keep doing exactly what the architecture already does — authoritative state-sync snapshots over
LiteNetLib — and grow it along the **Tribes → Quake 3 → Replication Graph** axis (object scoping,
acked-baseline deltas, grid AOI), smoothed by **entity interpolation**, measured by a **tick
budget**, persisted **memory-first/write-behind**, and split **spatially à la Ultima Online** only
when a profiler says so. Skip prediction, rollback, lag-comp, and determinism — the references say
they belong to other genres.
