# Protocol

The protocol is binary and versioned. It is intentionally small so packet behavior is easy to inspect.

## Movement Model

Movement is tile-stepped (the model landed at v9; the wire has since advanced — current shipped is **v15**, which replaced the per-step `MoveStep` stream with a held-direction `MoveIntent`, S43). Input is state, not events: the client sends `MoveIntent` with an input sequence, a `Moving` flag, and one 8-way `Direction` (`N`, `NE`, `E`, `SE`, `S`, `SW`, `W`, `NW`) — on change (keydown / keyup / direction change) plus a low-rate keepalive (~500 ms), not once per step. The server holds the latest intent per session and, each tick, for every session whose intent is `Moving` and whose step cooldown has elapsed, attempts exactly one tile step in `Direction` — validating cooldown, world bounds, and blocked tiles exactly as before. A blocked target keeps the intent (the entity steps once unblocked or redirected). `Moving=false` halts. A `Moving` session that goes silent (no intent for ~1 s) is force-stopped server-side. Stale intents (`Sequence <= lastSeq`) are ignored. There is still no client prediction; the server paces steps on its own cooldown, so step cadence no longer depends on client send timing. Rationale: [movement-input-model.md](movement-input-model.md).

Positions in `LoginResult`, `EntitySpawn`, and `WorldSnapshot` are integer tile coordinates. Entity snapshots also carry facing. Clients tween between confirmed tile centers; there is no client-side prediction. Rationale: [networking-design-plan.md](networking-design-plan.md) section 5a.

Step cadence is **per-entity** (S51, v16). Each `WorldEntity` carries a `SpeedMultiplier` (default `1.0`); the server derives an effective step cooldown (base cooldown ÷ multiplier, clamped to the configured min/max) and the tick loop paces that entity at its own effective cooldown rather than a single global one. Default `1.0` is byte-for-byte identical to the previous single-cadence behaviour. The cooldown is advertised on `EntitySpawn` and re-advertised via `MovementSpeedChanged` when it changes; the client tweens each entity at its advertised cadence, falling back to the `ServerHello` global when an entity carries no explicit value. This is still server-authoritative and prediction-free — only the cadence varies. The admin-gated `/speed <multiplier>` dev command sets the caller's own multiplier to exercise it end-to-end (item/buff-driven speed is a separate follow-up).

## Packet Envelope

Every payload encoded by `ProtocolCodec` starts with:

- `uint32` magic: `0x314F4D4D`
- `byte` version: `18` (current shipped — keep in sync with `ProtocolCodec.Version`; v18 added `ServerHello.turnDelayMs` — the authoritative turn delay so the client predictor mirrors turn timing, S63; v17 added the admin live-tuning message `AdminSetTuning`, S60; v16 added per-entity movement speed — an effective step cooldown on `EntitySpawn` plus the reliable `MovementSpeedChanged` message, S51; v15 replaced the per-step `MoveStep` stream with a held-direction `MoveIntent`, S43)
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
- `AdminSetTuning` (v17, S60): `string key`, `double value` — an admin-only request to set a live server tuning param. Reliable-ordered. The server **requires the session to be Admin** (the same role gate as `/speed` and `/metrics`); a non-admin request is **ignored** (logged, no reply, no disconnect). The key is looked up in a server-side registry that clamps/validates and applies it to a mutable runtime tuning holder (`ServerTuning`, seeded from `ServerOptions` at startup); an unknown/invalid key is ignored + logged. Starter keys: `move.stepCooldownMs` (global base step cooldown, clamped to `[50, 5000]` ms — the same bound `ServerOptions` validates), `move.turnDelayMs` (S63 turn delay, clamped to `[0, 1000]` ms — the same bound `ServerOptions` validates; the client mirrors it onto its predictor so the turn feel stays in lockstep), and `aoi.interestRadius` (clamped to `[1, 512]` tiles). The change takes effect on the next AOI pass / step; nothing is persisted (the panel finds values, defaults are baked in afterwards). There is no echo message in v1 — the client shows the value it sent; the server logs the post-clamp authoritative value.

## Server Messages

- `ServerHello`: server name, protocol version, tick rate, authoritative step cooldown in milliseconds, **turn delay in milliseconds** (v18, S63), and server interest radius in tiles. The turn delay is the cost of a facing change (turn-then-move): a turn frees the next step/turn after this delay instead of paying a full step cooldown. The client predictor adopts the advertised value (tick-quantised the same way the step cooldown is) so predicted turn timing stays in lockstep with the server; it is also live-tunable via `AdminSetTuning` (`move.turnDelayMs`).
- `LoginResult`: accepted/rejected, character id, display name, assigned role, spawn tile, reason.
- `ZoneInfo`: zone id, width, height, and a **procedural-terrain descriptor** — `int32 seed`, `int32 genVersion`, and `uint64 contentHash`. Static terrain is content, not state: rather than shipping the blocked-tile list, the server ships the seed and the client regenerates the identical map locally via the shared deterministic `TerrainGenerator` (`(width, height, seed, genVersion) -> blocked tiles`). The client compares its locally-computed hash to `contentHash` as a drift/tamper check and logs loudly on mismatch; the server remains authoritative for movement. Login terrain cost is constant regardless of map size. `genVersion` lets the generator algorithm change later without a silent mismatch.
- `EntitySpawn`: durable visible-entity metadata: network id, character id, kind, display name, initial tile, facing, and the entity's **effective step cooldown in milliseconds** (`uint16`, S51) so a viewer knows the entity's movement cadence the moment it sees it. The cooldown is the server's clamped per-entity value (base cooldown ÷ the entity's speed multiplier, clamped to the configured min/max), tick-quantised so it round-trips to the same tick count the client re-derives. Default speed (multiplier 1.0) yields the global cadence.
- `MovementSpeedChanged`: reliable-ordered notice (`uint32 networkId`, `uint16 stepCooldownMs`, S51) that an entity's effective step cadence changed mid-session (a speed buff/slow/mount applied or removed; the `/speed` dev command). Sent to every viewer whose area of interest currently includes the entity. Movement speed is kept **off** the hot `WorldSnapshot` path — cadence changes are rare relative to position updates — and rides this reliable message instead, like spawn/despawn. The client retunes that entity's tween cadence to the new cooldown (still no prediction; just confirmed-step tweening at the right speed).
- `EntityDespawn`: server tick plus network id for an entity that left the client's current area of interest.
- `WorldSnapshot`: server tick, per-client snapshot sequence, and compact visible entity state. Each entity state is `ushort networkId`, `int16 tileX`, `int16 tileY`, `byte facing`, `byte depleted` (the resource-node availability flag — false for players and all non-resource entities).
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
