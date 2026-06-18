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

public sealed record MoveInputMessage(uint Sequence, WorldVector Direction) : IProtocolMessage
{
    public MessageType Type => MessageType.MoveInput;
}

public sealed record ChatSendMessage(string Text) : IProtocolMessage
{
    public MessageType Type => MessageType.ChatSend;
}

public sealed record ServerHelloMessage(string ServerName, byte ProtocolVersion, int TickRate) : IProtocolMessage
{
    public MessageType Type => MessageType.ServerHello;
}

public sealed record LoginResultMessage(
    bool Accepted,
    Guid CharacterId,
    string DisplayName,
    ClientRole Role,
    WorldVector Position,
    string Reason) : IProtocolMessage
{
    public MessageType Type => MessageType.LoginResult;
}

public sealed record WorldSnapshotMessage(
    uint ServerTick,
    int TotalEntities,
    bool IsComplete,
    int ChunkIndex,
    int ChunkCount,
    IReadOnlyList<EntityStateSnapshot> Entities) : IProtocolMessage
{
    public WorldSnapshotMessage(uint serverTick, IReadOnlyList<EntityStateSnapshot> entities)
        : this(serverTick, entities.Count, true, 0, 1, entities)
    {
    }

    public WorldSnapshotMessage(
        uint serverTick,
        int totalEntities,
        bool isComplete,
        IReadOnlyList<EntityStateSnapshot> entities)
        : this(serverTick, totalEntities, isComplete, 0, 1, entities)
    {
    }

    public MessageType Type => MessageType.WorldSnapshot;
}

public sealed record EntitySpawnMessage(
    uint NetworkId,
    Guid CharacterId,
    EntityKind Kind,
    string DisplayName,
    WorldVector Position) : IProtocolMessage
{
    public MessageType Type => MessageType.EntitySpawn;
}

public sealed record EntityDespawnMessage(uint ServerTick, uint NetworkId) : IProtocolMessage
{
    public MessageType Type => MessageType.EntityDespawn;
}

public sealed record ChatBroadcastMessage(string Sender, string Text) : IProtocolMessage
{
    public MessageType Type => MessageType.ChatBroadcast;
}

public sealed record ServerErrorMessage(string Code, string Message) : IProtocolMessage
{
    public MessageType Type => MessageType.ServerError;
}
