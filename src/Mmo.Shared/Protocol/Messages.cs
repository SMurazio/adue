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

// S103 commit-step on release (protocol v21). A client→server request to finish a near-complete cosmetic step:
// when model B's render has glided past the commit threshold onto the NEXT tile at key-release, the client asks
// the server to step there for real (one tile in Direction) instead of snapping back. The server validates it
// like a normal step PLUS an anti-cheat floor (the entity must be at least CommitAcceptFraction of its cooldown
// into the current step) and, on accept, borrows the next step's cooldown so the average step rate can never
// exceed the normal cadence (no speedhack). There is no dedicated reply: the RESULT is observed via the normal
// snapshot stream — the confirmed tile advancing to the requested tile = accepted; staying = rejected. Sent
// reliable-ordered. Sequence rejects stale requests (seq <= lastMoveSeq), shared with the MoveIntent cursor.
public sealed record StepCommitRequestMessage(uint Sequence, Direction8 Direction) : IProtocolMessage
{
    public MessageType Type => MessageType.StepCommitRequest;
}

public sealed record ChatSendMessage(string Text) : IProtocolMessage
{
    public MessageType Type => MessageType.ChatSend;
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
