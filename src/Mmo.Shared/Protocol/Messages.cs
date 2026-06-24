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

// Held-direction movement intent (protocol v15, replaces the per-step MoveStep stream). Input as
// state, not events: the client declares what it intends (Moving + Direction) and the server steps
// the entity at its own cooldown cadence from that intent. Moving=false means stopped (Direction is
// then ignored). Sent reliable-ordered (a dropped "stop" must not be lost). Sequence rejects stale
// intents (seq <= lastSeq) belt-and-suspenders + anti-cheat. See docs/movement-input-model.md.
public sealed record MoveIntentMessage(uint Sequence, bool Moving, Direction8 Direction) : IProtocolMessage
{
    public MessageType Type => MessageType.MoveIntent;
}

// NET1 Stage 1 (protocol v23): loss-robust held-intent delivery — reliability via REDUNDANCY, not
// retransmission. Replaces the reliable-ordered MoveIntent send. Sent UNRELIABLE at a fixed rate
// (~20 Hz while moving, plus a short tail of Moving=false after stop). Every packet carries the FULL
// current intent (HeadSeq/Moving/Direction) so a lost packet is simply superseded by the next, PLUS a
// sliding Window of the last few prior inputs as deltas so an intermediate state change dropped on the
// wire is recovered from a later packet. The server dedupes by sequence (walk head+window, apply each
// seq > LastMoveSeq via the EXISTING held-intent path) — no head-of-line stall, no retransmit bunching.
// No authored ticks yet: this stage stays seq-based and server held-paced (those arrive in Stage 2+).
// Window entries are deltas off HeadSeq: SeqDelta is (HeadSeq - entry.Seq), so entry seq = HeadSeq -
// SeqDelta. The newest input is the head; Window holds strictly-older inputs in any order.
public readonly record struct MoveInputWindowEntry(byte SeqDelta, bool Moving, Direction8 Direction);

public sealed record MoveInputMessage(
    uint HeadSeq,
    bool Moving,
    Direction8 Direction,
    IReadOnlyList<MoveInputWindowEntry> Window) : IProtocolMessage
{
    public MessageType Type => MessageType.MoveInput;
}

// S103 commit-step on release (protocol v21). A client→server request to finish a near-complete cosmetic step:
// when model B's render has glided past the commit threshold onto the NEXT tile at key-release, the client asks
// the server to step there for real (one tile in Direction) instead of snapping back. The server validates it
// like a normal step PLUS an anti-cheat floor (the entity must be at least CommitAcceptFraction of its cooldown
// into the current step) and, on accept, borrows the next step's cooldown so the average step rate can never
// exceed the normal cadence (no speedhack). There is no dedicated reply: the RESULT is observed via the normal
// snapshot stream — the confirmed tile advancing to the requested tile = accepted; staying = rejected. Sent
// reliable-ordered. Sequence rejects stale requests on the server's COMMIT cursor (NET6 — a dedicated commit
// dedup cursor, separate from the MoveIntent cursor, so an intent seq can't burn an unconfirmed commit). The
// wire seq is still minted off the client's single shared monotonic counter; only the server bookkeeping splits.
public sealed record StepCommitRequestMessage(uint Sequence, Direction8 Direction) : IProtocolMessage
{
    public MessageType Type => MessageType.StepCommitRequest;
}

// NET2 (protocol v24) / NET3 (protocol v25): loss-robust UO commit delivery — the SAME redundancy-not-
// retransmission trick NET1 applied to held intent, now for the UoClientDriven per-step commit stream. Replaces
// the per-step reliable-ordered StepCommitRequest send. Sent UNRELIABLE: each packet carries the NEWEST committed
// step (HeadSeq + Direction) PLUS a sliding Window of the last few prior committed steps as deltas, so a dropped
// commit is recovered from a LATER packet's window (~one send interval late, spread out) instead of a reliable
// retransmit BATCH that the server's cooldown gate would reject all at once (the GodotB speed-up/desync).
//
// NET3 adds the AUTHORED TICK per commit: HeadTick is the integer server tick the predictor's gate banked the
// HEAD step on (the SAME tick the prediction advanced on — not a separately-sampled clock, which would reintroduce
// snapping). The server APPLIES each commit at its authored tick (gating/scheduling the cooldown on authored time,
// not receive time), so a bundled-recovered [C2,C3] no longer collides at one receive tick: C2 advances the
// eligible schedule to its authored end, and C3 (authored a cadence later) is then ACCEPTED instead of rejected as
// "too early". Window entries carry BOTH a SeqDelta (HeadSeq - entry.Seq) and a TickDelta (HeadTick - entry.Tick)
// off the head, so entry seq = HeadSeq - SeqDelta and entry authored tick = HeadTick - TickDelta. A commit has no
// Moving flag (it is always a step). The wire seq is minted off the client's single shared monotonic counter, but
// the SERVER dedups commits on a dedicated COMMIT cursor (NET6), separate from the MoveIntent/MoveInput cursor, so
// a higher-numbered intent (e.g. a keyup STOP) can no longer pre-dedup an unconfirmed commit's re-send.
public readonly record struct StepCommitWindowEntry(byte SeqDelta, uint TickDelta, Direction8 Direction);

public sealed record StepCommitBatchMessage(
    uint HeadSeq,
    uint HeadTick,
    Direction8 Direction,
    IReadOnlyList<StepCommitWindowEntry> Window) : IProtocolMessage
{
    public MessageType Type => MessageType.StepCommitBatch;
}

// UO1 client-driven movement mode signal (protocol v22). A one-bit declaration from the client that THIS session
// drives its own movement UO-style: the client predicts + banks tiles locally and sends one StepCommitRequest per
// accepted step, and the server must STOP auto-pacing the entity from the held MoveIntent (otherwise the held-
// intent pacer AND the per-step commits would both step the entity — double-stepping / 2x speed). ClientDriven=true
// enters the mode, false leaves it (reverts to server-paced held-intent stepping). The client keeps sending
// MoveIntent for stop/keepalive/facing regardless; the server simply ignores it for PACING while the flag is set.
// Sent reliable-ordered, and re-sent on (re)login / respawn so a lost flag can't silently double-step. See
// docs/uo-client-driven-mode-plan.md.
public sealed record MovementModeMessage(bool ClientDriven) : IProtocolMessage
{
    public MessageType Type => MessageType.MovementMode;
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

public sealed record ServerHelloMessage(string ServerName, byte ProtocolVersion, int TickRate, int StepCooldownMs, float InterestRadiusTiles) : IProtocolMessage
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
public sealed record WorldSnapshotMessage(
    uint ServerTick,
    uint SnapshotSequence,
    int TotalEntities,
    bool IsComplete,
    int ChunkIndex,
    int ChunkCount,
    IReadOnlyList<EntityStateSnapshot> Entities,
    uint RecipientStepSeq = 0) : IProtocolMessage
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
