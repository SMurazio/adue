using Mmo.Shared.Domain;

namespace Mmo.Shared.Protocol;

public interface IProtocolMessage
{
    MessageType Type { get; }
}

public sealed record ClientHelloMessage(string ClientName) : IProtocolMessage
{
    public MessageType Type => MessageType.ClientHello;
}

public sealed record LoginRequestMessage(string AccountName, string DisplayName) : IProtocolMessage
{
    public MessageType Type => MessageType.LoginRequest;
}

// CONTINUOUS MIGRATION (Phase 3, protocol v36): the per-INPUT continuous move intent (analog of the proven
// exp:ContinuousInput). The client sends ONE of these per RENDER FRAME with that frame's dt: a monotonic InputSeq,
// the RAW (un-normalized) world-axis direction the player is holding (DirX/DirY; WASD sums to {-1,0,1} per axis, a
// zero vector means STOP), and DtSeconds — how much sim-time that frame represents. The server integrates each
// FRESH input (InputSeq > session.LastInputSeq) by its own dt on the RECEIVE path (the experiment model), so the
// authoritative path matches the Phase-4 client predictor's replayed path under variable frame timing.
//
// ANTI-SPEEDHACK (server-side, SECURITY-CRITICAL): the client now controls dt, so the server CANNOT trust it. A
// per-input sanity clamp bounds DtSeconds into [0, ~0.25s], AND a per-peer WALL-CLOCK dt BUDGET caps the TOTAL
// integrated sim-time to real elapsed time (+ a small burst allowance for jitter). Net: over any window a peer's
// integrated distance cannot exceed real-time distance. See ClientSession.ConsumeMoveDtBudget + GameServer.
// Sent plain UNRELIABLE (freshness is gated by InputSeq server-side; a dropped input is superseded by the next frame's).
public sealed record MoveIntentMessage(uint InputSeq, float DirX, float DirY, float DtSeconds) : IProtocolMessage
{
    public MessageType Type => MessageType.MoveIntent;
}

// COMBAT-S2B (protocol v28): the first combat action — a client->server attack request on its OWN dedup
// cursor, entirely SEPARATE from movement's sequence (the NET6 lesson: two streams sharing one cursor stranded
// each other). The client mints Sequence off a DEDICATED _attackSeq counter (never _moveSequence) and the
// server dedups it on a DEDICATED _lastAttackSeq cursor (never _lastMoveSeq/_lastCommitSeq). Kind is the attack.
// Sent RELIABLE-ORDERED: attacks are low-rate, so reliable retransmit is fine and a dropped attack must not be
// lost (unlike movement's redundant-unreliable). The server validates the attack cooldown + resolves the hit +
// applies damage authoritatively; the result rides the existing public-HP snapshot field (no dedicated reply).
//
// FREEAIM (protocol v29): adds a continuous, quantized AIM ANGLE chosen by the client (the player→cursor world
// bearing), NOT a Direction8 — that continuity is the whole point of free aim. AimAngle is a ushort mapping the
// full 0..65535 range onto [0, 2π) (≈0.0055°/step), wrapping at the seam (65535 ≈ 359.99°). The server resolves
// a GEOMETRIC SECTOR (half-angle + radius) about this aim against entity world positions — no longer the
// facing-derived tile cone. The aim is a client-chosen continuous value the server validates by geometry
// (exactly like the move direction is a client-chosen value the server validates), so it stays server-authoritative.
//
// SWING-COMMIT-FIX (protocol v30): adds an AUTHORED TICK — the integer server tick the CLIENT stamped the swing on
// (its monotonic-clamped EstimateServerTick at send time, the SAME estimator the NET3 step-commit path uses). The
// swing ROOTS the attacker's movement, and BOTH sides must compute the identical root window or the predictor steps
// where the server will reject (the swing-then-move rubberband under latency). The pre-v30 server anchored the root
// on its RECEIVE tick (_serverTick), which under latency lands ~d ticks AFTER the predictor's send-time anchor → the
// server's root ends later → reject → rubberband. Carrying the authored tick lets the server root at the SAME logical
// tick the predictor did (clamped to a sane window around its own tick, like TryCommitStepAuthored bounds its
// authored tick), so server and predictor compute the identical root window regardless of arrival latency.
public sealed record AttackMessage(uint Sequence, AttackKind Kind, ushort AimAngle, uint AuthoredTick) : IProtocolMessage
{
    public MessageType Type => MessageType.Attack;
}

// MOVEMENT-ACTIONS Phase B1 (protocol v38): the client->server movement-action trigger — a SIBLING of the attack
// stream, NOT the move stream. Modeled byte-for-byte on AttackMessage (design §2.2): its own DEDICATED ActionSeq
// counter (client) dedup'd on a DEDICATED _lastActionSeq cursor (server) that shares NOTHING with the move or attack
// cursors (the NET6 "two streams, one cursor" lesson — a third stream gets a third cursor). Sent RELIABLE-ORDERED
// like Attack: actions are low-rate and a dropped trigger must not be lost.
//
//   ActionSeq    — the DEDICATED monotonic counter, dedup'd on _lastActionSeq.
//   ActionId     — the registry key (Jump=1) for the MovementActionDef to start (a byte; the codec range-validates it).
//   Heading      — the launch heading as a quantized world BEARING, reusing the SAME AimAngle ushort quantization the
//                  attack aim uses (0..65535 -> [0,2π), bearing atan2(dz,dx), +X east / +Z south). The wire carries a
//                  heading ONLY — never a height/distance/duration; those live in the server-side def (anti-cheat, §2.7).
//   AuthoredTick — the client's stamped server tick at trigger. It rides the wire for B2 (which will anchor the
//                  trajectory to the same logical tick the predictor did, like the swing-commit-fix); B1 does NOT
//                  consume it — B1 anchors the action at the SERVER RECEIPT tick (no prediction yet), so the client
//                  sends 0 (exactly like SendAttack today).
public sealed record ActionIntentMessage(uint ActionSeq, byte ActionId, ushort Heading, uint AuthoredTick) : IProtocolMessage
{
    public MessageType Type => MessageType.ActionIntent;
}

public sealed record ChatSendMessage(string Text) : IProtocolMessage
{
    public MessageType Type => MessageType.ChatSend;
}

// COMBAT-S1 (protocol v26): admin-gated client->server "set the CALLER's own local-player vital" verb. Stat is
// 0=Health, 1=Mana, 2=Stamina (mirrors WorldEntity.StatKind); Value is the desired CURRENT value (the server
// clamps into [0, max]). Reliable-ordered — a dropped set must not be lost. The server REQUIRES the session to be
// Admin (a non-admin request is ignored), exactly like /speed and AdminSetTuning. Drives the F7 dev-set window so
// the bars can be watched tracking min/max. No client-side prediction: the authoritative value lands back via the
// owner-only PlayerStatsMessage.
public sealed record AdminSetStatMessage(byte Stat, int Value) : IProtocolMessage
{
    public MessageType Type => MessageType.AdminSetStat;
}

// S60 admin live-tuning: a generic "set this server param to this value" verb, reliable-ordered. Key is a
// short registry key (e.g. "move.stepCooldownMs", "aoi.interestRadius"); Value is the desired value (the
// server clamps/validates against the registry). The server REQUIRES the session to be Admin — a non-admin
// request is ignored. Generalizes the bespoke /speed command into a data-driven panel knob. Ephemeral by
// design: nothing is persisted; the panel finds values, the Orchestrator bakes winners into defaults.
public sealed record AdminSetTuningMessage(string Key, double Value) : IProtocolMessage
{
    public MessageType Type => MessageType.AdminSetTuning;
}

// MONSTER-TUNING-SAVE (protocol v42): admin-gated, PARAMETERLESS client->server command — "persist the current live
// monster-type tuning to disk". The server serializes the live MonsterType values back to the data manifest
// (Content/monsters.json, the file LoadMonsterTypes reads at startup) so live tweaks made via AdminSetTuning survive a
// restart, completing the tune-live → Save → persisted loop. Reliable-ordered. The server REQUIRES the session to be
// Admin (the same gate as AdminSetTuning — this WRITES A FILE from a network command); a non-admin send is ignored +
// logged. No payload: the command carries nothing; the server reads the authoritative live registry.
public sealed record SaveMonsterTuningMessage : IProtocolMessage
{
    public MessageType Type => MessageType.SaveMonsterTuning;
}

// PLAYER-COLLISION-TOGGLE (protocol v43): admin-gated client->server request to flip PLAYER↔PLAYER collision — whether
// OTHER PLAYERS are collision obstacles. Enabled=true ⇒ players collide (the shipped default); false ⇒ players pass
// through each other. Server-authoritative + admin-gated (the same gate as AdminSetTuning) because it affects EVERYONE:
// the server flips the flag on its Zone, then broadcasts the new value (PlayerCollisionSettingMessage) to ALL clients so
// every client predictor's obstacle gather and the server integrator's gather flip TOGETHER (prediction parity — a
// client-only flag would rubber-band). Monster collision (player↔monster + monster↔monster) is unaffected either way.
// Reliable-ordered — a dropped toggle must not be lost. A non-admin send is ignored + logged.
public sealed record AdminSetPlayerCollisionMessage(bool Enabled) : IProtocolMessage
{
    public MessageType Type => MessageType.AdminSetPlayerCollision;
}

// PLAYER-COLLISION-TOGGLE (protocol v43): server->client replication of the authoritative player↔player collision flag.
// Sent on login (initial truth) and broadcast on every change, so the client's obstacle gather gates on the SAME value
// the server integrator does (prediction parity — see AdminSetPlayerCollisionMessage). Monster collision is unaffected;
// this flag gates ONLY whether OTHER PLAYERS are obstacles. Reliable-ordered, global (not AOI-scoped).
public sealed record PlayerCollisionSettingMessage(bool Enabled) : IProtocolMessage
{
    public MessageType Type => MessageType.PlayerCollisionSetting;
}

public sealed record SnapshotAckMessage(uint LastSnapshotSequence) : IProtocolMessage
{
    public MessageType Type => MessageType.SnapshotAck;
}

// Generic client->server "use the entity I'm pointing at" verb. Carries only the target's network id;
// the server resolves what interacting means (harvest for now) and validates authority/adjacency.
public sealed record InteractRequestMessage(uint TargetNetworkId) : IProtocolMessage
{
    public MessageType Type => MessageType.InteractRequest;
}

// Server->owner acknowledgement of an InteractRequest. Reason is a short machine-readable code on
// failure (e.g. "too_far", "depleted", "not_resource", "no_target") and empty on success.
public sealed record InteractResultMessage(bool Success, string Reason) : IProtocolMessage
{
    public MessageType Type => MessageType.InteractResult;
}

// Server->owner private inventory delta: the changed stacks (Quantity is the new authoritative total
// for each template; 0 means the stack is now empty). Owner-only — inventory never AOI-replicates.
public sealed record InventoryUpdateMessage(IReadOnlyList<ItemStack> ChangedStacks) : IProtocolMessage
{
    public MessageType Type => MessageType.InventoryUpdate;
}

// CONTINUOUS MIGRATION (Phase 4, v37): BodyRadiusUnits is the server's authoritative player body radius (the live
// ServerTuning.BodyRadiusUnits admin knob, default CollisionDefaults.BodyRadius=0.5), replicated so the client predictor
// collides against EXACTLY the radius the server integrates with. Without it the client would silently assume the
// default and desync at every wall the instant the knob moves (one of the three Phase-4 determinism gaps).
public sealed record ServerHelloMessage(string ServerName, byte ProtocolVersion, int TickRate, int StepCooldownMs, float InterestRadiusUnits, float BodyRadiusUnits) : IProtocolMessage
{
    public MessageType Type => MessageType.ServerHello;
}

public sealed record LoginResultMessage(
    bool Accepted,
    Guid CharacterId,
    string DisplayName,
    ClientRole Role,
    TileCoord Tile,
    string Reason) : IProtocolMessage
{
    public MessageType Type => MessageType.LoginResult;
}

// RecipientStepSeq (S76, protocol v19) is a per-snapshot HEADER field scoped to the recipient's OWN entity:
// the value of that entity's WorldEntity.StepSequence (accepted-tile-move count) at snapshot-build time. It is
// recipient metadata, NOT a per-entity field — it rides every snapshot to a client (real-delta AND empty
// keep-alive) even when the recipient's own entity is delta'd out of the payload because it is idle. This
// stage only emits it; the client decodes it but does not yet reconcile against it (S77). Defaults to 0 in the
// convenience constructors (tests / non-recipient-scoped uses).
//
// CONTINUOUS MIGRATION (Phase 3, v36): LastInputSeq is a SECOND recipient-scoped header field, riding right after
// RecipientStepSeq — the highest per-input MoveIntent seq the server has INTEGRATED for the recipient (session
// .LastInputSeq) at build time. Like RecipientStepSeq it rides every snapshot (real-delta AND keep-alive) so the
// Phase-4 client predictor can trim/replay its unacked input buffer against the server's integrated cursor. The
// Phase-3 client stores it (unused until Phase 4 adds prediction). Defaults to 0 in the convenience constructors.
public sealed record WorldSnapshotMessage(
    uint ServerTick,
    uint SnapshotSequence,
    int TotalEntities,
    bool IsComplete,
    int ChunkIndex,
    int ChunkCount,
    IReadOnlyList<EntityStateSnapshot> Entities,
    uint RecipientStepSeq = 0,
    uint LastInputSeq = 0) : IProtocolMessage
{
    public WorldSnapshotMessage(uint serverTick, IReadOnlyList<EntityStateSnapshot> entities)
        : this(serverTick, 0, entities.Count, true, 0, 1, entities)
    {
    }

    public WorldSnapshotMessage(uint serverTick, uint snapshotSequence, IReadOnlyList<EntityStateSnapshot> entities)
        : this(serverTick, snapshotSequence, entities.Count, true, 0, 1, entities)
    {
    }

    public WorldSnapshotMessage(
        uint serverTick,
        int totalEntities,
        bool isComplete,
        IReadOnlyList<EntityStateSnapshot> entities)
        : this(serverTick, 0, totalEntities, isComplete, 0, 1, entities)
    {
    }

    public WorldSnapshotMessage(
        uint serverTick,
        uint snapshotSequence,
        int totalEntities,
        bool isComplete,
        IReadOnlyList<EntityStateSnapshot> entities)
        : this(serverTick, snapshotSequence, totalEntities, isComplete, 0, 1, entities)
    {
    }

    public MessageType Type => MessageType.WorldSnapshot;
}

// MONSTER-BEHAVIOR P6 (protocol v41, docs/monster-behavior-design.md): EntitySpawn now carries a PLACEHOLDER per-type
// VISUAL — a replicated TintRgb (0xRRGGBB; 0xFFFFFF = white = no tint) + ScaleMilli (render scale × 1000; 1000 = 1.0 =
// unchanged) the client applies to the entity's visual node so a type renders visibly distinct (a gnoll bigger +
// tinted) with NO art assets. Monsters set these from their MonsterType; every other kind (players/dummies/resources/
// corpses) ships the defaults (0xFFFFFF / 1000) so its render is byte-identical. This is the replicated hook where real
// per-type models/animations slot in later (the client-side tint/scale mapping is replaced; the wire fields stay).
public sealed record EntitySpawnMessage(
    uint NetworkId,
    Guid CharacterId,
    EntityKind Kind,
    string DisplayName,
    TileCoord Tile,
    Direction8 Facing,
    ushort StepCooldownMs,
    uint TintRgb = 0xFFFFFFu,
    ushort ScaleMilli = 1000) : IProtocolMessage
{
    public MessageType Type => MessageType.EntitySpawn;
}

// Reliable-ordered notice that an entity's effective step cadence changed mid-session (a speed buff
// applied/removed, /speed dev command, etc.). Sent to every viewer whose AOI currently includes the
// entity so the client can retune that entity's tween cadence. Speed is kept OFF the hot WorldSnapshot
// path — cadence changes are rare relative to position updates — and rides this reliable message
// instead, like spawn/despawn. StepCooldownMs is the entity's clamped effective per-step cooldown.
public sealed record MovementSpeedChangedMessage(uint NetworkId, ushort StepCooldownMs) : IProtocolMessage
{
    public MessageType Type => MessageType.MovementSpeedChanged;
}

// COMBAT-S1 (protocol v26): server->owner replication of the LOCAL player's vitals (HP/mana/stamina, each
// current + max). Owner-only and reliable-ordered, like InventoryUpdate / MovementSpeedChanged — vitals change
// rarely relative to position, so they ride this dedicated message and stay OFF the hot WorldSnapshot path. Sent
// once on login (initial truth) and again whenever the values change (the dev-set window for now; damage/heal/
// regen later). Other entities' vitals are a later stage — this stage replicates the recipient's own only.
public sealed record PlayerStatsMessage(CharacterStats Stats) : IProtocolMessage
{
    public MessageType Type => MessageType.PlayerStats;
}

// COMBAT-TUNING (protocol v31): server->client replication of the live combat feel-knobs. The combat values are
// server-authoritative (resolved in HandleAttack + FreeAimSectorResolver) and live-tunable via AdminSetTuning
// (combat.* registry keys); this message ships the CURRENT snapshot so the client's free-aim wedge mesh, swing-root
// prediction, and radial cooldown indicator all derive from the SAME numbers the server resolves with — killing the
// earlier client/server constant duplication (where the wedge could disagree with the real danger area). Sent to
// each client on login (initial truth) and broadcast to all authenticated clients whenever a combat.* key changes.
// Reliable-ordered, like PlayerStats/MovementSpeedChanged — it changes rarely and must never be lost.
public sealed record CombatTuningMessage(CombatTuningSnapshot Tuning) : IProtocolMessage
{
    public MessageType Type => MessageType.CombatTuning;
}

// COMBAT-QOL (protocol v32): a server->client COSMETIC damage event. Emitted by HandleAttack whenever a free-aim
// hit ACTUALLY reduced a victim's HP (no event for a 0-damage / already-dead hit, and never for regen), and sent
// AOI-gated to every viewer that can currently SEE the victim — exactly like MovementSpeedChanged is scoped to the
// entity's viewers. The client floats a red "-Amount" number above that entity (presentation only). NetworkId is the
// VICTIM's id; Amount is the HP actually removed this hit; Health is the victim's NEW current HP after the hit (so a
// late/odd-ordered event can't drive the number off a stale bar — though the authoritative bar still rides the
// snapshot). Sent UNRELIABLE: a dropped damage number is purely cosmetic and the next snapshot already carries the
// true HP, so reliable retransmit would only add latency for no gameplay benefit.
public sealed record DamageEventMessage(uint NetworkId, int Amount, ushort Health) : IProtocolMessage
{
    public MessageType Type => MessageType.DamageEvent;
}

// LIVING-ENEMIES P2-POLISH (protocol v33): server->client replication of the per-monster-TYPE tuning. The monster AI
// tuning is server-authoritative + live-tunable via AdminSetTuning on the per-type "<typeId>.<field>" keys; this
// ships the CURRENT per-type values so the F1 "Monster" tab can list the types and show + edit the authoritative
// numbers (mirroring CombatTuningMessage). Sent to each client on login (initial truth) and broadcast to all
// authenticated clients whenever a per-type key changes. Reliable-ordered, like CombatTuning — rare and must not be
// lost. The client derives NO simulation from it; it exists purely for the admin tuning panel.
public sealed record MonsterTuningMessage(MonsterTuningSnapshot Tuning) : IProtocolMessage
{
    public MessageType Type => MessageType.MonsterTuning;
}

// LIVING-ENEMIES P3 (protocol v34): server->viewer replication of a SPAWNER's red-tile marker — the PERSISTENT
// leash/de-aggro anchor that owns + respawns a monster. Replaces the former per-monster MonsterHomeMessage: the marker
// is keyed by a stable SpawnerId (NOT a monster network id, which is reborn on each respawn), so the red tile STAYS PUT
// while the monster dies and a fresh one spawns. Active=true is sent when the spawner enters a viewer's AOI (place the
// red tile); Active=false when it leaves AOI (drop it). Tile is the spawner's fixed tile. Reliable-ordered; AOI-driven
// per-recipient like the entity spawn/despawn pair. The monster's leash HOME is exactly this spawner tile.
public sealed record SpawnerMarkerMessage(uint SpawnerId, TileCoord Tile, bool Active) : IProtocolMessage
{
    public MessageType Type => MessageType.SpawnerMarker;
}

// LOOT P4c (protocol v35): the loot-window verb a client sends against a corpse it has OPEN. Kind selects the
// action: TakeItem takes the single stack identified by TemplateKey, LootAll takes everything that fits, Close
// releases the window (the server forgets the open-loot pairing). TemplateKey is meaningful only for TakeItem (it
// is empty otherwise). OPENING the window is NOT a LootAction — it reuses the existing InteractRequest on a corpse
// (the same E-key path that targets corpses), so the open path needs no new client input. Reliable-ordered: a
// dropped take or close must not be lost (loot is low-rate, so reliable retransmit is fine — unlike movement).
public sealed record LootActionMessage(uint CorpseNetworkId, LootActionKind Kind, string TemplateKey) : IProtocolMessage
{
    public MessageType Type => MessageType.LootAction;
}

// LOOT P4c (protocol v35): server->owner replication of an OPEN corpse's live contents — what the loot window
// lists. Open=true carries the current remaining stacks (template key + quantity + rarity tier) for the corpse the
// owner just opened or just looted from; the window shows/refreshes to these. Open=false (empty Items) tells the
// client to CLOSE the window — the last item was taken / loot-all emptied it / the player walked out of range / the
// corpse decayed. CorpseNetworkId ties the payload to the targeted corpse entity so a stale window for a different
// corpse can't be confused. Owner-only + reliable-ordered, like InventoryUpdate (corpse loot never AOI-replicates).
public sealed record CorpseContentsMessage(uint CorpseNetworkId, bool Open, IReadOnlyList<CorpseItem> Items) : IProtocolMessage
{
    public MessageType Type => MessageType.CorpseContents;
}

public sealed record EntityDespawnMessage(uint ServerTick, uint NetworkId) : IProtocolMessage
{
    public MessageType Type => MessageType.EntityDespawn;
}

// TELEGRAPH T2 (protocol v44, docs/ability-telegraph-sync-design.md): server->client announcement of a SCHEDULED
// ground telegraph — the wire half of the deadline-form sync. TelegraphId is the scheduler's monotonic id (keys the
// client decal and dedupes a re-send); Shape is the LOCKED cast-time shape (kind + origin + radius — the codec ships
// the origin as Q12.4 fixed-point exactly like snapshot positions, and the radius as a Q12.4 ushort, so the drawn
// decal matches the server's danger area to 1/16 unit); StartTick/ResolveTick are ABSOLUTE server ticks. Every client
// renders the fill as progress = (estimatedNow − StartTick)/(ResolveTick − StartTick) clamped [0,1] against its
// COSMETIC server-clock estimate and self-resolves at T — so all viewers land on T at the same wall-clock instant and
// a late AOI joiner (who receives this mid-windup) renders the correct REMAINING fill from the same two ticks.
//
// Deliberately MINIMAL: no resolve/cancel/despawn counterpart exists. Resolution is client-local at T (the whole point
// of the deadline form — the authoritative outcome rides the normal damage/HP replication), and T1 decided a telegraph
// OUTLIVES its caster, so nothing can cancel one mid-windup. Sent RELIABLE-ORDERED (a dropped telegraph is a hit with
// no warning — never acceptable), AOI-scoped per recipient at schedule time + on AOI-enter via the known-id diff pass
// (the SpawnerMarker pattern; the active set is tiny because telegraphs live ~1.5 s).
public sealed record TelegraphMessage(ulong TelegraphId, TelegraphShape Shape, uint StartTick, uint ResolveTick) : IProtocolMessage
{
    public MessageType Type => MessageType.Telegraph;
}

// ECOLOGY E4 (protocol v45, docs/ecology-v1-design.md D5/D6, §3/§8 E4): server->client replication of ONE
// authored ecology region's current legible state — the wire half of the "read the world before you walk there"
// pillar. RegionId/DisplayName/the tile rect are the region's IMMUTABLE authored geometry (from EcologyRegistry);
// Types is one {typeId, state} entry per monster type the region hosts. D5: fuzzy words, never numbers — no
// stock/pressure value ever rides this message, only the five-state EcologyPopulationState enum.
//
// Sent to every authenticated client: the FULL set (one RegionEcologyMessage per authored region) on login, and
// a single RE-SEND of just the changed region whenever ANY of its type-states flips (compared once per
// EcologyTick + once per RecordKill — state flips are rare, so this carries ~zero steady-state traffic, like
// MonsterTuning/SpawnerMarker). Reliable-ordered, GLOBAL (not AOI-scoped, like PlayerCollisionSetting/
// CombatTuning) — legibility is a pre-walk read, so every client needs every region regardless of proximity.
//
// MinTileX/MinTileY/MaxTileX/MaxTileY are the region's INCLUSIVE tile rect (mirrors EcologyRegion's own
// MinX/MinY/MaxX/MaxY) — the minimap overlay draws exactly this rect, tinted by the region's WORST type-state
// (EcologyLegibility.WorstOf).
public sealed record RegionEcologyMessage(
    string RegionId,
    string DisplayName,
    int MinTileX,
    int MinTileY,
    int MaxTileX,
    int MaxTileY,
    IReadOnlyList<RegionEcologyTypeEntry> Types) : IProtocolMessage
{
    public MessageType Type => MessageType.RegionEcology;
}

// One monster type's replicated legibility state within a region. TypeId is the monster-type registry key
// (matches MonsterTuningMessage's per-type Id); State is the D5 five-state enum — the ONLY ecology signal that
// ever reaches a client (D5: fuzzy words, never numbers).
public readonly record struct RegionEcologyTypeEntry(string TypeId, EcologyPopulationState State);

// Terrain is procedural content, not state: instead of shipping the blocked-tile list, ZoneInfo carries
// a tiny descriptor — dimensions plus the generator (Seed, GenVersion) — and a ContentHash of the
// generated blocked set. The client regenerates the identical map locally via the shared
// TerrainGenerator and compares hashes as a drift/tamper check. Login terrain cost is now ~constant
// regardless of map size. The server remains authoritative for movement validation.
//
// NODE-FIELD N2 (protocol v46, docs/node-field-design.md D2): CatalogHash is the SAME drift-guard
// discipline applied to the shared NodeCatalog — the client independently builds the identical catalogue
// from (ZoneId's zone Seed, the same regenerated AuthoredMap) and compares. A mismatch means the client's
// scatter code has drifted from the server's (or the map/class table did) — see MmoClient.HandleZoneInfo
// for the comparison (mirrors the ContentHash check exactly: loud diagnostic, not a connection-level
// hard-fail — the server stays authoritative for the actual harvest regardless).
public sealed record ZoneInfoMessage(
    string ZoneId,
    int Width,
    int Height,
    int Seed,
    int GenVersion,
    ulong ContentHash,
    ulong CatalogHash) : IProtocolMessage
{
    public MessageType Type => MessageType.ZoneInfo;
}

// NODE-FIELD N2 (protocol v46, docs/node-field-design.md D3/D4): server->client announcement that ONE
// catalogue node's availability flipped — a harvest (Depleted=true) or a respawn (Depleted=false).
// NodeIndex is the catalogue's stable ushort index (NEVER a position — see NodeCatalog's D1 rationale).
// Sent reliable-ordered, GLOBAL (not AOI-scoped, like RegionEcology/PlayerCollisionSetting): D4 reasons
// that at community scale a harvest event is tiny (~5 bytes) and player-paced, so per-session AOI diffing
// buys nothing over just telling everyone.
public sealed record NodeStateMessage(ushort NodeIndex, bool Depleted) : IProtocolMessage
{
    public MessageType Type => MessageType.NodeState;
}

// NODE-FIELD N2 (protocol v46, docs/node-field-design.md D4): sent ONCE on login — the field's full set of
// current EXCEPTIONS, i.e. only the currently-DEPLETED indices (typically a handful among thousands; the
// vast majority of untouched nodes need no wire representation at all — the whole point of the catalogue
// architecture). A joining client's rendered field starts correct without a per-node payload. Reliable-
// ordered.
public sealed record NodeStateBatchMessage(IReadOnlyList<ushort> DepletedIndices) : IProtocolMessage
{
    public MessageType Type => MessageType.NodeStateBatch;
}

// NODE-FIELD N2 (protocol v46, docs/node-field-design.md D5): client->server harvest request, targeting a
// catalogue INDEX — the node-field replacement for InteractRequest's former resource-harvest branch
// (InteractRequest still exists, but now only ever resolves a corpse-open; harvestable nodes are no longer
// WorldEntities an InteractRequest can name). The server validates range/availability/reach exactly as the
// entity path did (see GameServer.HandleHarvestNode) and replies via the SAME owner-only InteractResult
// (reused verbatim — the reason-code vocabulary is unchanged). Reliable-ordered, like InteractRequest.
public sealed record HarvestNodeMessage(ushort NodeIndex) : IProtocolMessage
{
    public MessageType Type => MessageType.HarvestNode;
}

// DUO-SKILLSHOT (protocol v47, exp/duo-abilities): the client->server "fire my fusion skillshot" trigger. A SIBLING
// of the attack/action streams (NOT the move stream): its OWN dedicated Sequence counter (client) dedup'd on a
// DEDICATED _lastFireSeq cursor (server) that shares NOTHING with the move/attack/action cursors (the NET6 third-
// stream lesson). AimAngle is the launch bearing, reusing the SAME AimAngle ushort quantization the attack aim uses
// (0..65535 -> [0,2π), atan2(dz,dx), +X east / +Z south). The server spawns a straight-line projectile from the
// shooter's position along this heading. Sent RELIABLE-ORDERED like Attack — low-rate, and a dropped fire must not
// be lost.
public sealed record FireSkillshotMessage(uint Sequence, ushort AimAngle) : IProtocolMessage
{
    public MessageType Type => MessageType.FireSkillshot;
}

// DUO-SKILLSHOT (protocol v47): the aim-preview relay, travelling BOTH directions with one shape. Client->server: the
// shooter sends its current aim Heading while HOLDING the fire key (throttled ~8Hz, only while a partner exists);
// ShooterNetworkId is 0 (the server knows the sender). Server->partner: the server relays it with ShooterNetworkId
// set to the sender's network id so the partner draws the faint intercept-preview line from that shooter's position
// along Heading. Active=false is the release edge (stop drawing). Sent UNRELIABLE: a dropped preview frame is
// harmless and superseded by the next.
public sealed record AimPreviewMessage(uint ShooterNetworkId, ushort Heading, bool Active) : IProtocolMessage
{
    public MessageType Type => MessageType.AimPreview;
}

// DUO-SKILLSHOT (protocol v47): server->client replication of a player's PAIR state — the FOUNDATION seam abilities
// 2-4 also consume. PartnerNetworkId is the partner player's entity network id (meaningful only when Paired); Paired
// is the mutual-pair flag. Sent to BOTH players when a /pair is established (each learns the other's id for the
// intercept previews and future co-op cues) and Paired=false to the surviving partner when the pair breaks (/unpair
// or a disconnect). Owner-only, reliable-ordered (a dropped pair edge would leave a client's partner state stale).
public sealed record PairStatusMessage(uint PartnerNetworkId, bool Paired) : IProtocolMessage
{
    public MessageType Type => MessageType.PairStatus;
}

public sealed record ChatBroadcastMessage(string Sender, string Text) : IProtocolMessage
{
    public MessageType Type => MessageType.ChatBroadcast;
}

public sealed record ServerErrorMessage(string Code, string Message) : IProtocolMessage
{
    public MessageType Type => MessageType.ServerError;
}
