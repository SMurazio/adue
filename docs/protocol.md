# Protocol

The protocol is binary and versioned. It is intentionally small so packet behavior is easy to inspect.

## Movement Model

Movement is **continuous and server-authoritative** (protocol v36; the tile-stepped model that lived from v9 was deleted then — there is no step commit, movement mode, or per-tile cadence on the wire). The client samples input once per render frame and sends one `MoveIntent` per frame: a monotonic `InputSeq`, the raw held world-axis direction (`DirX`/`DirY`; a zero vector means stop), and `DtSeconds` — how much sim-time that frame represents. The server integrates each **fresh** input (`InputSeq > LastInputSeq`; stale/duplicate sequences are ignored) by its own dt on the receive path: position += `unitDir × SpeedUnitsPerSecond × dt`, resolved through the shared swept-circle collision (walls + entity obstacles). Anti-speedhack: the client controls dt, so the server never trusts it — each input's dt is sanity-clamped to `[0, 0.25 s]` (`ContinuousMovement.MaxInputDtSeconds`, the same clamp the client predictor and send path apply) and then debited against a per-peer **wall-clock dt budget** that accrues real elapsed time (capped by a ~0.4 s burst allowance), so over any window a peer's integrated sim-time cannot exceed real time + the allowance.

The client **predicts and reconciles**: it applies each input locally the frame it is sent, buffers it, and on every snapshot replays the inputs newer than the snapshot's `LastInputSeq` (a recipient-scoped header field — the highest input seq the server has integrated for that client) on top of the authoritative position. The determinism inputs the predictor needs are replicated: the authoritative body radius (`ServerHello.BodyRadiusUnits`, v37) and the live player↔player collision flag (`PlayerCollisionSetting`, v43). Remote entities interpolate between snapshot samples and, when samples starve, dead-reckon along the entity's replicated `Velocity` (v39), capped (~250 ms) so a signal loss parks instead of flinging. Positions on the wire are fixed-point **Q12.4** (`PositionEncoding`: two signed shorts of sixteenths of a unit, quantized on send only — the server's double position is never rounded back). 1 unit = one tile-width; the tile grid survives as the map/collision content layer, not as a movement quantum.

Movement speed is a continuous stat: the server derives each entity's `SpeedUnitsPerSecond` = base move speed × the entity's `SpeedMultiplier` (base = `1000 / StepCooldownMs` units/sec, live-tunable as `continuous.baseMoveSpeed`; the `/speed` dev command sets a player's multiplier). On the wire, per-entity speed is still advertised as an **effective step cooldown in ms** on `EntitySpawn` and re-advertised via `MovementSpeedChanged` (units/sec = 1000 ÷ cooldown), which keeps speed off the hot snapshot path.

## Packet Envelope

Every payload encoded by `ProtocolCodec` starts with:

- `uint32` magic: `0x314F4D4D`
- `byte` version: `43` (current shipped — keep in sync with `ProtocolCodec.Version`)
- `uint16` message type
- message-specific payload

The transport is LiteNetLib:

- reliable ordered delivery for login, chat, entity spawn/despawn metadata, admin verbs, attacks/actions/loot, and the tuning/settings replication messages
- unreliable delivery for `MoveIntent` (one per render frame — a dropped input is superseded by the next frame's; freshness is gated by `InputSeq`), `WorldSnapshot`, and the cosmetic `DamageEvent`
- sequenced delivery for `SnapshotAck`

World snapshots should fit in a single UDP packet for the current channel target. Entity identity is sent separately with `EntitySpawn`; the hot `WorldSnapshot` path carries only a channel-local network id, the quantized continuous position, facing, and the compact per-entity state below. Snapshots are **acked-baseline deltas** (S46): each has a per-client sequence number, the client acks the highest **contiguously received** sequence via `SnapshotAck`, and the server sends an entity only if its state revision differs from the one that client last acked — the ack is load-bearing, not advisory. A **moving** entity (and one running a movement action) is force-included every tick regardless of revision, which is what keeps remote motion fluid in the continuous model (the deliberate per-mover bandwidth trade). Prolonged ack silence forces a re-baseline.

`WorldSnapshot` is therefore usually incomplete (`isComplete=false`) and contains only the entities the recipient needs this tick; clients merge deltas into their current visible set. A complete snapshot occurs when a delta happens to carry every visible entity (first snapshot / re-baseline). When nothing changed, a low-rate **empty** keep-alive is sent instead (there is no periodic full heartbeat). `EntityDespawn` tells a client that an entity left its area of interest (radius-based with a visible-entity cap). The development target is roughly 120-150 **visible** players per channel — a conservative floor, not a measured ceiling (`capacity-ladder-study.md`, S40).

## Version History

- v15 (S43): held-direction `MoveIntent` replaced the per-step `MoveStep` stream.
- v16 (S51): per-entity movement speed — effective step cooldown on `EntitySpawn` + `MovementSpeedChanged`.
- v17 (S60): admin live-tuning message `AdminSetTuning`.
- v18 (S63): `ServerHello.turnDelayMs` *(removed in v20)*.
- v19 (S76): per-recipient `RecipientStepSeq` header on `WorldSnapshot`.
- v20 (S98): removed `turnDelayMs` — turn-then-move deleted; a direction change steps immediately.
- v21 (S103): client→server `StepCommitRequest` — commit-step on release *(deleted at v36)*.
- v22 (UO1): client→server `MovementMode` one-bit client-driven signal *(deleted at v36)*.
- v23 (NET1): redundant-unreliable `MoveInput` held-intent channel *(deleted at v36)*.
- v24 (NET2): redundant-unreliable `StepCommitBatch` commit channel *(deleted at v36)*.
- v25 (NET3): authored tick (`HeadTick`/`TickDelta`) on `StepCommitBatch` *(deleted at v36)*.
- v26 (COMBAT-S1): admin-gated `AdminSetStat` + owner-only `PlayerStats` vitals replication.
- v27 (COMBAT-S2A): public HP (`Health`/`MaxHealth`) on the entity snapshot for the overhead bar.
- v28 (COMBAT-S2B): client→server `AttackMessage` (attack seq + kind on its own dedup cursor).
- v29 (FREEAIM): quantized continuous aim angle (`ushort` → [0, 2π)) on `AttackMessage`.
- v30 (SWING-COMMIT-FIX): authored tick on `AttackMessage` (server roots the swing at the client's logical tick).
- v31 (COMBAT-TUNING): server→client `CombatTuningMessage` (live combat feel-knobs).
- v32 (COMBAT-QOL): server→client unreliable `DamageEventMessage` (floating "-N", AOI-gated).
- v33 (LIVING-ENEMIES P2): server→client `MonsterTuningMessage` (per-type tuning for the F1 Monster tab).
- v34 (LIVING-ENEMIES P3): spawner-keyed `SpawnerMarkerMessage` (stable spawner id + Active flag) replaced per-monster `MonsterHome`.
- v35 (LOOT P4c): corpse loot window — client→server `LootActionMessage` + server→owner `CorpseContentsMessage`.
- v36 (CONTINUOUS MIGRATION): the continuous wire break — Q12.4 fixed-point positions on the snapshot, per-input `MoveIntent` `{InputSeq, DirX, DirY, DtSeconds}` with the dt-budget anti-speedhack, `LastInputSeq` snapshot header for reconcile; ALL tile-step machinery (`MoveInput`, `StepCommitRequest`, `StepCommitBatch`, `MovementMode`) deleted, tags 8-11 left as gaps.
- v37: `BodyRadiusUnits` replicated on `ServerHello` (client-predictor collision parity).
- v38 (MOVEMENT-ACTIONS B1): client→server `ActionIntentMessage` (actionSeq, actionId, quantized heading, authored tick) + optional replicated `VerticalOffset` on the entity snapshot (absent ⇒ grounded).
- v39 (REMOTE-WALK): per-entity `Velocity` replicated in the snapshot under a combined flags byte — the wire for remote dead-reckoning.
- v40: `MonsterTuningMessage` reshaped to a DATA-DRIVEN generic field list (`{Key, Label, Value, Min, Max, IsInteger}` per knob) — a new server knob needs no protocol bump.
- v41 (MONSTER-BEHAVIOR P6): placeholder per-type visual on `EntitySpawn` — `TintRgb` (uint 0xRRGGBB) + `ScaleMilli` (ushort, scale×1000).
- v42 (MONSTER-TUNING-SAVE): parameterless, admin-gated `SaveMonsterTuningMessage` — F1 Save persists live monster tuning to `Content/monsters.json`.
- v43 (PLAYER-COLLISION-TOGGLE): admin-gated client→server `AdminSetPlayerCollisionMessage` + server→client `PlayerCollisionSettingMessage` (sent on login + broadcast on change) — live server-authoritative player↔player collision toggle, default ON; monster collision unaffected.

## Client Messages

- `ClientHello`: optional client name/diagnostics.
- `LoginRequest`: dev account name and display name.
- `MoveIntent` (v36): the per-input continuous move — `uint InputSeq`, `float DirX`/`DirY` (raw held world-axis direction; zero vector = stop), `float DtSeconds`. Sent unreliable once per render frame; the server integrates each fresh input by its (clamped, budget-debited) dt. See Movement Model.
- `Attack` / `AttackMessage` (v28/v29/v30): attack sequence (its OWN dedup cursor, never movement's), attack kind, quantized aim angle (`ushort` 0..65535 → [0, 2π) — the player→cursor bearing the server resolves a geometric sector against), and an authored tick (the client-stamped server tick the swing roots movement at). Reliable-ordered.
- `ActionIntent` / `ActionIntentMessage` (v38): movement-action trigger (jump) on its OWN dedup cursor — `uint ActionSeq`, `byte ActionId` (registry key; Jump=1), `ushort Heading` (same quantization as the aim angle), `uint AuthoredTick`. Heights/distances/durations live in the server-side action def, never on the wire (anti-cheat). Reliable-ordered.
- `ChatSend`: text chat for the current zone. Slash-prefixed text is interpreted as a server command after authentication.
- `SnapshotAck`: the highest **contiguously received** `WorldSnapshot` sequence (the gap-free prefix — not simply the latest to arrive). Drives the server's acked-baseline delta selection. Sequenced.
- `InteractRequest`: network id of the target entity (generic verb; harvest and corpse-open are the resolutions). The server validates authentication, AOI-visibility, and a Euclidean reach of ≤ 1.5 units between the continuous positions (`InteractionTuning.InteractionRadiusUnits` — shared with the client's targeting so reach never drifts).
- `LootAction` / `LootActionMessage` (v35): loot-window verb on an OPEN corpse — `TakeItem` (one stack by template key), `LootAll`, or `Close`. Opening reuses `InteractRequest`. Reliable-ordered.
- `AdminSetStat` (v26): admin-gated "set my local player's current vital" (`byte` stat: HP/mana/stamina, `int` value; server clamps to [0, max]). Drives the dev-set window.
- `AdminSetTuning` (v17): `string key`, `double value` — admin-only live server tuning. The key is looked up in a server-side registry that clamps/validates (e.g. `continuous.baseMoveSpeed`, `aoi.interestRadius`, `combat.*`, `<monsterTypeId>.<field>`); an unknown/invalid key or a non-admin sender is ignored + logged. Nothing is persisted (see `SaveMonsterTuning`).
- `SaveMonsterTuning` (v42): parameterless, admin-gated — persist the live monster TYPE tuning to `Content/monsters.json` so it survives a restart. Reliable-ordered.
- `AdminSetPlayerCollision` (v43): admin-gated `bool Enabled` — flip player↔player collision live for everyone (default ON). The server flips its zone flag and broadcasts `PlayerCollisionSetting` so client predictors and the server integrator gate on the same value. Reliable-ordered.

Tags 8-11 are numeric gaps (the deleted v21-v25 tile-step machinery); survivors were not renumbered.

## Server Messages

- `ServerHello`: server name, protocol version, tick rate, base step cooldown in ms (the base-speed anchor: base units/sec = 1000 ÷ this), interest radius in units, and the authoritative player body radius in units (v37, predictor collision parity).
- `LoginResult`: accepted/rejected, character id, display name, assigned role, spawn tile, reason.
- `ZoneInfo`: zone id, width, height, and a **procedural-terrain descriptor** — `int32 seed`, `int32 genVersion`, `uint64 contentHash`. The client regenerates the identical map locally via the shared deterministic `TerrainGenerator` and compares hashes as a drift/tamper check; the server remains authoritative for movement.
- `EntitySpawn`: durable visible-entity metadata — network id, character id, kind, display name, initial tile, facing, the entity's **effective step cooldown in ms** (`ushort`; the per-entity speed encoding — units/sec = 1000 ÷ cooldown), and the placeholder per-type visual `TintRgb` + `ScaleMilli` (v41).
- `MovementSpeedChanged`: reliable notice (`uint32 networkId`, `uint16 stepCooldownMs`) that an entity's effective speed changed mid-session (speed multiplier change, monster-type tuning edit, `/speed`). Sent to every viewer whose AOI includes the entity; keeps speed off the hot snapshot path.
- `EntityDespawn`: server tick plus network id for an entity that left the client's area of interest.
- `WorldSnapshot`: server tick, per-client snapshot sequence, `RecipientStepSeq` (legacy recipient header, v19), `LastInputSeq` (recipient header, v36 — the reconcile cursor), chunk metadata, and compact visible-entity state. Each entity state is `ushort networkId`, Q12.4 fixed-point position (two `int16`s of sixteenths of a unit), `byte facing`, `byte depleted` (resource-node availability), `ushort health` + `ushort maxHealth` (v27; 0/0 = no HP bar), then a flags byte (v39): bit0 airborne ⇒ a Q12.4 `ushort` `VerticalOffset` follows (v38); bit1 moving ⇒ `int16` velX, velY in 1/256 units/sec follow (v39). A resting grounded entity pays 1 tail byte.
- `InteractResult`: success flag plus a short reason code (`too_far`, `depleted`, `not_resource`, `no_target`, `inventory_full`, `rate_limited`, …; empty on success). Owner-only.
- `InventoryUpdate`: owner-only private inventory delta — the changed stacks with new authoritative totals (0 = emptied). Never AOI-replicated.
- `ChatBroadcast`: sender plus text.
- `PlayerStats` (v26): server→owner replication of the local player's vitals (HP/mana/stamina, current + max). Owner-only.
- `CombatTuning` / `CombatTuningMessage` (v31): live combat feel-knobs (attack cooldown ms, swing-root ms, sector half-angle deg / radius units, damage). Sent on login + on every `combat.*` change so the client's wedge/predictor/cooldown-viz match the server's resolution.
- `DamageEvent` / `DamageEventMessage` (v32): cosmetic damage event (victim network id + amount + new health), AOI-gated to the victim's viewers, for the floating "-N". Unreliable — authoritative HP rides the snapshot.
- `MonsterTuning` / `MonsterTuningMessage` (v33; data-driven at v40): per-monster-TYPE tuning — each type carries a stable `Id`, `DisplayName`, and a generic list of fields `{Key, Label, Value, Min, Max, IsInteger}`. The F1 Monster tab renders a row per field and applies via `AdminSetTuning("<typeId>.<Key>", value)`, so a new server knob needs no client or protocol change. Sent on login + on change.
- `SpawnerMarker` / `SpawnerMarkerMessage` (v34): a SPAWNER's red-tile marker — the persistent leash/respawn anchor, keyed by a stable spawner id with an `Active` flag (true on AOI-entry, false on AOI-exit); survives monster death/respawn. Reliable.
- `CorpseContents` / `CorpseContentsMessage` (v35): server→owner contents of an OPEN corpse (template key + quantity + rarity per stack), re-sent after each take/loot-all; `Open=false` closes the window. Owner-only + reliable-ordered.
- `PlayerCollisionSetting` (v43): the authoritative player↔player collision flag (`bool`). Sent on login + broadcast on every change (global, not AOI-scoped) so every client's obstacle gather matches the server integrator's. Reliable-ordered.
- `ServerError`: code and message.

## Rules

- The server may reject invalid protocol versions.
- Movement is validated server-side: per-input dt sanity clamp + per-peer wall-clock dt budget (anti-speedhack), world bounds, and the shared swept-circle collision resolve (walls + entity obstacles).
- Snapshot positions are server-owned truth, quantized to Q12.4 on send only — the server's full-precision position is never rounded back into the sim.
- Snapshot acknowledgements drive the shipped acked-baseline delta selection (S46) — an entity is re-sent until the client acks the snapshot revision that carried it; ack silence forces a re-baseline.
- Snapshot chunks may be split when the packet budget requires it; clients should assemble chunks for the same tick before treating a snapshot as complete.
- Chat text is length-limited by the codec and should be sanitized before any rich client renders it.
