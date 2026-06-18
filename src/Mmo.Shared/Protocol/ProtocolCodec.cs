using System.Text;
using Mmo.Shared.Domain;

namespace Mmo.Shared.Protocol;

public static class ProtocolCodec
{
    public const uint Magic = 0x314F4D4D;
    public const byte Version = 8;

    private const int MaxStringBytes = 2048;
    private const int MaxSnapshotEntities = 4096;
    private const float SnapshotPositionScale = 10f;

    public static byte[] Encode(IProtocolMessage message)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);

        writer.Write(Magic);
        writer.Write(Version);
        writer.Write((ushort)message.Type);

        switch (message)
        {
            case ClientHelloMessage value:
                WriteString(writer, value.ClientName);
                break;
            case LoginRequestMessage value:
                WriteString(writer, value.AccountName);
                WriteString(writer, value.DisplayName);
                break;
            case MoveInputMessage value:
                writer.Write(value.Sequence);
                WriteVector(writer, value.Direction);
                break;
            case ChatSendMessage value:
                WriteString(writer, value.Text);
                break;
            case SnapshotAckMessage value:
                writer.Write(value.LastSnapshotSequence);
                break;
            case ServerHelloMessage value:
                WriteString(writer, value.ServerName);
                writer.Write(value.ProtocolVersion);
                writer.Write(value.TickRate);
                break;
            case LoginResultMessage value:
                writer.Write(value.Accepted);
                WriteGuid(writer, value.CharacterId);
                WriteString(writer, value.DisplayName);
                writer.Write((byte)value.Role);
                WriteVector(writer, value.Position);
                WriteString(writer, value.Reason);
                break;
            case WorldSnapshotMessage value:
                writer.Write(value.ServerTick);
                writer.Write(value.SnapshotSequence);
                WriteSnapshotMetadata(writer, value);
                WriteEntityStates(writer, value.Entities);
                break;
            case EntitySpawnMessage value:
                writer.Write(value.NetworkId);
                WriteGuid(writer, value.CharacterId);
                writer.Write((byte)value.Kind);
                WriteString(writer, value.DisplayName);
                WriteVector(writer, value.Position);
                break;
            case EntityDespawnMessage value:
                writer.Write(value.ServerTick);
                writer.Write(value.NetworkId);
                break;
            case ChatBroadcastMessage value:
                WriteString(writer, value.Sender);
                WriteString(writer, value.Text);
                break;
            case ServerErrorMessage value:
                WriteString(writer, value.Code);
                WriteString(writer, value.Message);
                break;
            default:
                throw new ProtocolException($"Unsupported message type {message.GetType().Name}.");
        }

        writer.Flush();
        return stream.ToArray();
    }

    public static IProtocolMessage Decode(ReadOnlySpan<byte> packet)
    {
        using var stream = new MemoryStream(packet.ToArray(), writable: false);
        using var reader = new BinaryReader(stream, Encoding.UTF8);

        if (reader.ReadUInt32() != Magic)
        {
            throw new ProtocolException("Invalid packet magic.");
        }

        var version = reader.ReadByte();
        if (version != Version)
        {
            throw new ProtocolException($"Unsupported protocol version {version}.");
        }

        var type = (MessageType)reader.ReadUInt16();
        return type switch
        {
            MessageType.ClientHello => new ClientHelloMessage(ReadString(reader)),
            MessageType.LoginRequest => new LoginRequestMessage(ReadString(reader), ReadString(reader)),
            MessageType.MoveInput => new MoveInputMessage(reader.ReadUInt32(), ReadVector(reader)),
            MessageType.ChatSend => new ChatSendMessage(ReadString(reader)),
            MessageType.SnapshotAck => new SnapshotAckMessage(reader.ReadUInt32()),
            MessageType.ServerHello => new ServerHelloMessage(ReadString(reader), reader.ReadByte(), reader.ReadInt32()),
            MessageType.LoginResult => new LoginResultMessage(
                reader.ReadBoolean(),
                ReadGuid(reader),
                ReadString(reader),
                (ClientRole)reader.ReadByte(),
                ReadVector(reader),
                ReadString(reader)),
            MessageType.WorldSnapshot => ReadWorldSnapshot(reader),
            MessageType.ChatBroadcast => new ChatBroadcastMessage(ReadString(reader), ReadString(reader)),
            MessageType.ServerError => new ServerErrorMessage(ReadString(reader), ReadString(reader)),
            MessageType.EntitySpawn => new EntitySpawnMessage(
                reader.ReadUInt32(),
                ReadGuid(reader),
                (EntityKind)reader.ReadByte(),
                ReadString(reader),
                ReadVector(reader)),
            MessageType.EntityDespawn => new EntityDespawnMessage(reader.ReadUInt32(), reader.ReadUInt32()),
            _ => throw new ProtocolException($"Unknown message type {(ushort)type}.")
        };
    }

    private static void WriteEntityStates(BinaryWriter writer, IReadOnlyList<EntityStateSnapshot> entities)
    {
        if (entities.Count > MaxSnapshotEntities)
        {
            throw new ProtocolException($"Snapshot has too many entities: {entities.Count}.");
        }

        writer.Write((ushort)entities.Count);
        foreach (var entity in entities)
        {
            writer.Write(ToSnapshotNetworkId(entity.NetworkId));
            writer.Write(QuantizeSnapshotCoordinate(entity.Position.X));
            writer.Write(QuantizeSnapshotCoordinate(entity.Position.Y));
        }
    }

    private static void WriteSnapshotMetadata(BinaryWriter writer, WorldSnapshotMessage snapshot)
    {
        if (snapshot.TotalEntities < snapshot.Entities.Count || snapshot.TotalEntities > MaxSnapshotEntities)
        {
            throw new ProtocolException($"Invalid snapshot total entity count: {snapshot.TotalEntities}.");
        }

        if (snapshot.ChunkCount < 1 || snapshot.ChunkIndex < 0 || snapshot.ChunkIndex >= snapshot.ChunkCount)
        {
            throw new ProtocolException($"Invalid snapshot chunk {snapshot.ChunkIndex}/{snapshot.ChunkCount}.");
        }

        writer.Write((ushort)snapshot.TotalEntities);
        writer.Write(snapshot.IsComplete);
        writer.Write((ushort)snapshot.ChunkIndex);
        writer.Write((ushort)snapshot.ChunkCount);
    }

    private static WorldSnapshotMessage ReadWorldSnapshot(BinaryReader reader)
    {
        var tick = reader.ReadUInt32();
        var sequence = reader.ReadUInt32();
        var totalEntities = reader.ReadUInt16();
        var isComplete = reader.ReadBoolean();
        var chunkIndex = reader.ReadUInt16();
        var chunkCount = reader.ReadUInt16();
        var entities = ReadEntityStates(reader);
        if (totalEntities < entities.Count)
        {
            throw new ProtocolException($"Snapshot total {totalEntities} is smaller than payload count {entities.Count}.");
        }

        if (chunkCount < 1 || chunkIndex >= chunkCount)
        {
            throw new ProtocolException($"Invalid snapshot chunk {chunkIndex}/{chunkCount}.");
        }

        return new WorldSnapshotMessage(tick, sequence, totalEntities, isComplete, chunkIndex, chunkCount, entities);
    }

    private static IReadOnlyList<EntityStateSnapshot> ReadEntityStates(BinaryReader reader)
    {
        var count = reader.ReadUInt16();
        if (count > MaxSnapshotEntities)
        {
            throw new ProtocolException($"Snapshot has too many entities: {count}.");
        }

        var entities = new List<EntityStateSnapshot>(count);
        for (var i = 0; i < count; i++)
        {
            var networkId = reader.ReadUInt16();
            var x = DequantizeSnapshotCoordinate(reader.ReadInt16());
            var y = DequantizeSnapshotCoordinate(reader.ReadInt16());
            entities.Add(new EntityStateSnapshot(networkId, new WorldVector(x, y)));
        }

        return entities;
    }

    private static ushort ToSnapshotNetworkId(uint networkId)
    {
        if (networkId > ushort.MaxValue)
        {
            throw new ProtocolException($"Snapshot network id is out of range: {networkId}.");
        }

        return (ushort)networkId;
    }

    private static short QuantizeSnapshotCoordinate(float value)
    {
        if (!float.IsFinite(value))
        {
            throw new ProtocolException("Snapshot coordinate must be finite.");
        }

        var scaled = MathF.Round(value * SnapshotPositionScale, MidpointRounding.AwayFromZero);
        if (scaled < short.MinValue || scaled > short.MaxValue)
        {
            throw new ProtocolException($"Snapshot coordinate is out of range: {value}.");
        }

        return (short)scaled;
    }

    private static float DequantizeSnapshotCoordinate(short value)
    {
        return value / SnapshotPositionScale;
    }

    private static void WriteVector(BinaryWriter writer, WorldVector value)
    {
        writer.Write(value.X);
        writer.Write(value.Y);
    }

    private static WorldVector ReadVector(BinaryReader reader)
    {
        return new WorldVector(reader.ReadSingle(), reader.ReadSingle());
    }

    private static void WriteGuid(BinaryWriter writer, Guid value)
    {
        writer.Write(value.ToByteArray());
    }

    private static Guid ReadGuid(BinaryReader reader)
    {
        var bytes = reader.ReadBytes(16);
        if (bytes.Length != 16)
        {
            throw new ProtocolException("Invalid GUID payload.");
        }

        return new Guid(bytes);
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length > MaxStringBytes)
        {
            throw new ProtocolException($"String payload is too large: {bytes.Length} bytes.");
        }

        writer.Write((ushort)bytes.Length);
        writer.Write(bytes);
    }

    private static string ReadString(BinaryReader reader)
    {
        var length = reader.ReadUInt16();
        if (length > MaxStringBytes)
        {
            throw new ProtocolException($"String payload is too large: {length} bytes.");
        }

        var bytes = reader.ReadBytes(length);
        if (bytes.Length != length)
        {
            throw new ProtocolException("String payload ended early.");
        }

        return Encoding.UTF8.GetString(bytes);
    }
}
