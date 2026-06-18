# Protocol

The first protocol is binary and versioned. It is intentionally small so packet behavior is easy to inspect.

## Packet Envelope

Every payload encoded by `ProtocolCodec` starts with:

- `uint32` magic: `0x314F4D4D`
- `byte` version: `7`
- `uint16` message type
- message-specific payload

The transport is LiteNetLib:

- reliable ordered delivery for login, chat, and entity spawn metadata
- unreliable delivery for compact world movement snapshots
- sequenced or delta movement can be introduced after the basic loop is stable

Movement snapshots should fit in a single UDP packet for the current channel target. Entity identity is sent separately with `EntitySpawn`; the hot `WorldSnapshot` path carries only a channel-local network id plus quantized position. `EntityDespawn` tells a client that an entity left its current area of interest. Clients should remove the rendered object/list row but may keep cached metadata for faster re-entry. The server first applies per-client area-of-interest selection, currently radius based with a visible-entity cap. The current development target is roughly 120-150 connected clients visible in one channel.

## Client Messages

- `ClientHello`: optional client name/diagnostics.
- `LoginRequest`: dev account name and display name.
- `MoveInput`: input sequence plus direction vector.
- `ChatSend`: text chat for the current zone. Slash-prefixed text is interpreted as a server command after authentication.

## Server Messages

- `ServerHello`: server name, protocol version, tick rate.
- `LoginResult`: accepted/rejected, character id, display name, assigned role, spawn position, reason.
- `EntitySpawn`: durable visible-entity metadata: network id, character id, kind, display name, and initial position.
- `EntityDespawn`: server tick plus network id for an entity that left the client's current area of interest.
- `WorldSnapshot`: server tick plus compact visible entity state. Each entity state is `ushort networkId`, `int16 x`, `int16 y`; positions are fixed point at 0.1 world-unit precision.
- `ChatBroadcast`: sender plus text.
- `ServerError`: code and message.

## Rules

- The server may reject invalid protocol versions.
- Movement direction is normalized server-side.
- Snapshot positions are server-owned truth.
- Snapshot chunks may be split when the packet budget requires it; clients should assemble chunks for the same tick before treating a snapshot as complete.
- Chat text is length-limited by the codec and should be sanitized before any rich client renders it.
