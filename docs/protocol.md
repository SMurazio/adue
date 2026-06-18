# Protocol

The protocol is binary and versioned. It is intentionally small so packet behavior is easy to inspect.

## Movement Model

Protocol v12 uses tile-stepped movement. The client sends `MoveStep` with an input sequence and one 8-way `Direction` (`N`, `NE`, `E`, `SE`, `S`, `SW`, `W`, `NW`). The server validates cooldown, world bounds, and blocked tiles, then moves the entity exactly one tile if the step is legal.

Positions in `LoginResult`, `EntitySpawn`, and `WorldSnapshot` are integer tile coordinates. Entity snapshots also carry facing. Clients tween between confirmed tile centers; there is no client-side prediction. Rationale: [networking-design-plan.md](networking-design-plan.md) section 5a.

## Packet Envelope

Every payload encoded by `ProtocolCodec` starts with:

- `uint32` magic: `0x314F4D4D`
- `byte` version: `12`
- `uint16` message type
- message-specific payload

The transport is LiteNetLib:

- reliable ordered delivery for login, chat, and entity spawn/despawn metadata
- unreliable delivery for compact world snapshots
- sequenced delivery for movement steps and snapshot acknowledgements

World snapshots should fit in a single UDP packet for the current channel target. Entity identity is sent separately with `EntitySpawn`; the hot `WorldSnapshot` path carries only a channel-local network id, tile coordinates, and facing. Each snapshot has a per-client sequence number, and clients send `SnapshotAck` with the latest sequence they received. The ack is harmless under full snapshots today and exists to unlock delta-against-acked-baseline snapshots later.

Between full heartbeat snapshots, `WorldSnapshot` may be incomplete (`isComplete=false`) and contain only visible entities whose tile/facing changed for that recipient. Clients merge incomplete snapshots into their current visible set. Full heartbeat snapshots remain self-contained.

`EntityDespawn` tells a client that an entity left its current area of interest. Clients should remove the rendered object/list row but may keep cached metadata for faster re-entry. The server first applies per-client area-of-interest selection, currently radius based with a visible-entity cap. Unchanged snapshots are skipped except for a low-rate heartbeat. The current development target is roughly 120-150 connected clients visible in one channel.

## Client Messages

- `ClientHello`: optional client name/diagnostics.
- `LoginRequest`: dev account name and display name.
- `MoveStep`: input sequence plus 8-way direction.
- `ChatSend`: text chat for the current zone. Slash-prefixed text is interpreted as a server command after authentication.
- `SnapshotAck`: latest `WorldSnapshot` sequence received by the client.

## Server Messages

- `ServerHello`: server name, protocol version, tick rate, authoritative step cooldown in milliseconds, and server interest radius in tiles.
- `LoginResult`: accepted/rejected, character id, display name, assigned role, spawn tile, reason.
- `ZoneInfo`: zone id, width, height, and blocked-tile map. The codec carries blocked tiles as a compact bitset ordered row-major by tile coordinate; clients render the server-provided map instead of duplicating wall seeds.
- `EntitySpawn`: durable visible-entity metadata: network id, character id, kind, display name, initial tile, and facing.
- `EntityDespawn`: server tick plus network id for an entity that left the client's current area of interest.
- `WorldSnapshot`: server tick, per-client snapshot sequence, and compact visible entity state. Each entity state is `ushort networkId`, `int16 tileX`, `int16 tileY`, `byte facing`.
- `ChatBroadcast`: sender plus text.
- `ServerError`: code and message.

## Rules

- The server may reject invalid protocol versions.
- Movement steps are validated server-side against cooldown, bounds, and blocked tiles.
- Snapshot tile coordinates are server-owned truth.
- Snapshot acknowledgements are advisory; full snapshots remain self-contained until delta snapshots are explicitly added.
- Snapshot chunks may be split when the packet budget requires it; clients should assemble chunks for the same tick before treating a snapshot as complete.
- Chat text is length-limited by the codec and should be sanitized before any rich client renders it.
