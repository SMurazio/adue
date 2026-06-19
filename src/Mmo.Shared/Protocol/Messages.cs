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

public sealed record ChatSendMessage(string Text) : IProtocolMessage
{
    public MessageType Type => MessageType.ChatSend;
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

public sealed record WorldSnapshotMessage(
    uint ServerTick,
    uint SnapshotSequence,
    int TotalEntities,
    bool IsComplete,
    int ChunkIndex,
    int ChunkCount,
    IReadOnlyList<EntityStateSnapshot> Entities) : IProtocolMessage
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
    Direction8 Facing) : IProtocolMessage
{
    public MessageType Type => MessageType.EntitySpawn;
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
