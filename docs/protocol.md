# Protocol

The protocol is binary and versioned. It is intentionally small so packet behavior is easy to inspect.

## Movement Model

Movement is tile-stepped (the model landed at v9; the wire has since advanced — current shipped is **v15**, which replaced the per-step `MoveStep` stream with a held-direction `MoveIntent`, S43). Input is state, not events: the client sends `MoveIntent` with an input sequence, a `Moving` flag, and one 8-way `Direction` (`N`, `NE`, `E`, `SE`, `S`, `SW`, `W`, `NW`) — on change (keydown / keyup / direction change) plus a low-rate keepalive (~500 ms), not once per step. The server holds the latest intent per session and, each tick, for every session whose intent is `Moving` and whose step cooldown has elapsed, attempts exactly one tile step in `Direction` — validating cooldown, world bounds, and blocked tiles exactly as before. A blocked target keeps the intent (the entity steps once unblocked or redirected). `Moving=false` halts. A `Moving` session that goes silent (no intent for ~1 s) is force-stopped server-side. Stale intents (`Sequence <= lastSeq`) are ignored. There is still no client prediction; the server paces steps on its own cooldown, so step cadence no longer depends on client send timing. Rationale: [movement-input-model.md](movement-input-model.md).

Positions in `LoginResult`, `EntitySpawn`, and `WorldSnapshot` are integer tile coordinates. Entity snapshots also carry facing. Clients tween between confirmed tile centers; there is no client-side prediction. Rationale: [networking-design-plan.md](networking-design-plan.md) section 5a.

## Packet Envelope

Every payload encoded by `ProtocolCodec` starts with:

- `uint32` magic: `0x314F4D4D`
- `byte` version: `16` (current shipped — keep in sync with `ProtocolCodec.Version`; v16 delta-codes the per-entity snapshot row — step-delta positions + a changed-field bitmask, S47b. v15 was held-direction `MoveIntent`, S43.)
- `uint16` message type
- message-specific payload

The transport is LiteNetLib:

- reliable ordered delivery for login, chat, entity spawn/despawn metadata, and `MoveIntent` (a dropped "stop" must not be lost)
- unreliable delivery for compact world snapshots
- sequenced delivery for snapshot acknowledgements

World snapshots should fit in a single UDP packet for the current channel target. Entity identity is sent separately with `EntitySpawn`; the hot `WorldSnapshot` path carries only a channel-local network id, tile coordinates, and facing. Each snapshot has a per-client sequence number, and clients send `SnapshotAck` with the latest sequence they received. The ack is harmless under full snapshots today and exists to unlock delta-against-acked-baseline snapshots later.

Between full heartbeat snapshots, `WorldSnapshot` may be incomplete (`isComplete=false`) and contain only visible entities whose tile/facing changed for that recipient. Clients merge incomplete snapshots into their current visible set. Full heartbeat snapshots remain self-contained.

`EntityDespawn` tells a client that an entity left its current area of interest. Clients should remove the rendered object/list row but may keep cached metadata for faster re-entry. The server first applies per-client area-of-interest selection, currently radius based with a visible-entity cap. Unchanged snapshots are skipped except for a low-rate heartbeat. The development target is roughly 120-150 **visible** players per channel — a conservative floor, not a measured ceiling: the capacity ladder (`capacity-ladder-study.md`, S40) shows server CPU is far from the bound and the real limiters are per-client bandwidth and the AOI scan at high *visible* density.

## Client Messages

- `ClientHello`: optional client name/diagnostics.
- `LoginRequest`: dev account name and display name.
- `MoveIntent`: input sequence, a `Moving` flag, and an 8-way direction (held-direction intent; the server steps the entity from this at its own cooldown cadence). `Moving=false` = stopped (direction ignored).
- `ChatSend`: text chat for the current zone. Slash-prefixed text is interpreted as a server command after authentication.
- `SnapshotAck`: latest `WorldSnapshot` sequence received by the client.
- `InteractRequest`: network id of the target entity (generic verb; harvest is the first resolution). The server validates authentication, AOI-visibility, and ≤1-tile adjacency before resolving.

## Server Messages

- `ServerHello`: server name, protocol version, tick rate, authoritative step cooldown in milliseconds, and server interest radius in tiles.
- `LoginResult`: accepted/rejected, character id, display name, assigned role, spawn tile, reason.
- `ZoneInfo`: zone id, width, height, and a **procedural-terrain descriptor** — `int32 seed`, `int32 genVersion`, and `uint64 contentHash`. Static terrain is content, not state: rather than shipping the blocked-tile list, the server ships the seed and the client regenerates the identical map locally via the shared deterministic `TerrainGenerator` (`(width, height, seed, genVersion) -> blocked tiles`). The client compares its locally-computed hash to `contentHash` as a drift/tamper check and logs loudly on mismatch; the server remains authoritative for movement. Login terrain cost is constant regardless of map size. `genVersion` lets the generator algorithm change later without a silent mismatch.
- `EntitySpawn`: durable visible-entity metadata: network id, character id, kind, display name, initial tile, and facing.
- `EntityDespawn`: server tick plus network id for an entity that left the client's current area of interest.
- `WorldSnapshot`: server tick, per-client snapshot sequence, and compact visible entity state, **delta-coded against the baseline the client has acked** (v16, S47b). Each entity row is `ushort networkId` + a **changed-field bitmask** (`PositionStep` | `PositionAbsolute` | `Facing` | `Depleted`) followed by only the changed fields: position is a 1-byte `Direction8` **step** for a one-tile move, absolute `int16 x,y` on a baseline/AOI-entry/non-unit move, omitted when unchanged; `facing`/`depleted` ride the bitmask. A **complete** snapshot (AOI entry / force-re-baseline) sends every field absolute to (re)establish the baseline. Step deltas are emitted only when the move is exactly one unit from the **acked** baseline (which the highest-contiguous ack, S47a, guarantees the client holds), so a lost snapshot freezes the baseline → the next move is non-unit → absolute → self-corrects (no cumulative-delta corruption). The client sends `SnapshotAck` with the **highest contiguously-received** sequence; the server advances each viewer's baseline to what acked snapshots carried.
- `InteractResult`: success flag plus a short reason code (`too_far`, `depleted`, `not_resource`, `no_target`, `inventory_full`, `rate_limited`, …; empty on success). Sent to the requesting owner only.
- `InventoryUpdate`: owner-only private inventory delta — the changed stacks, each carrying the new authoritative total quantity (0 = emptied). Never AOI-replicated.
- `ChatBroadcast`: sender plus text.
- `ServerError`: code and message.

## Rules

- The server may reject invalid protocol versions.
- Movement steps are validated server-side against cooldown, bounds, and blocked tiles.
- Snapshot tile coordinates are server-owned truth.
- Snapshot acknowledgements are advisory; full snapshots remain self-contained until delta snapshots are explicitly added.
- Snapshot chunks may be split when the packet budget requires it; clients should assemble chunks for the same tick before treating a snapshot as complete.
- Chat text is length-limited by the codec and should be sanitized before any rich client renders it.
