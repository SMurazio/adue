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
// Sent UNRELIABLE-sequenced (latest frame wins; a dropped input is superseded by the next frame's).
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
public sealed record ServerHelloMessage(string ServerName, byte ProtocolVersion, int TickRate, int StepCooldownMs, float InterestRadiusTiles, float BodyRadiusUnits) : IProtocolMessage
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

public sealed record EntitySpawnMessage(
    uint NetworkId,
    Guid CharacterId,
    EntityKind Kind,
    string DisplayName,
    TileCoord Tile,
    Direction8 Facing,
    ushort StepCooldownMs) : IProtocolMessage
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

// Terrain is procedural content, not state: instead of shipping the blocked-tile list, ZoneInfo carries
// a tiny descriptor — dimensions plus the generator (Seed, GenVersion) — and a ContentHash of the
// generated blocked set. The client regenerates the identical map locally via the shared
// TerrainGenerator and compares hashes as a drift/tamper check. Login terrain cost is now ~constant
// regardless of map size. The server remains authoritative for movement validation.
public sealed record ZoneInfoMessage(
    string ZoneId,
    int Width,
    int Height,
    int Seed,
    int GenVersion,
    ulong ContentHash) : IProtocolMessage
{
    public MessageType Type => MessageType.ZoneInfo;
}

public sealed record ChatBroadcastMessage(string Sender, string Text) : IProtocolMessage
{
    public MessageType Type => MessageType.ChatBroadcast;
}

public sealed record ServerErrorMessage(string Code, string Message) : IProtocolMessage
{
    public MessageType Type => MessageType.ServerError;
}
