using System.Text;
using Mmo.Shared.Domain;

namespace Mmo.Shared.Protocol;

public static class ProtocolCodec
{
    public const uint Magic = 0x314F4D4D;
    public const byte Version = 9;

    private const int MaxStringBytes = 2048;
    private const int MaxSnapshotEntities = 4096;

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
            case MoveStepMessage value:
                writer.Write(value.Sequence);
                writer.Write((byte)value.Direction);
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
                WriteTile(writer, value.Tile);
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
                WriteTile(writer, value.Tile);
                writer.Write((byte)value.Facing);
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
            MessageType.MoveStep => new MoveStepMessage(reader.ReadUInt32(), ReadDirection(reader)),
            MessageType.ChatSend => new ChatSendMessage(ReadString(reader)),
            MessageType.SnapshotAck => new SnapshotAckMessage(reader.ReadUInt32()),
            MessageType.ServerHello => new ServerHelloMessage(ReadString(reader), reader.ReadByte(), reader.ReadInt32()),
            MessageType.LoginResult => new LoginResultMessage(
                reader.ReadBoolean(),
                ReadGuid(reader),
                ReadString(reader),
                (ClientRole)reader.ReadByte(),
                ReadTile(reader),
                ReadString(reader)),
            MessageType.WorldSnapshot => ReadWorldSnapshot(reader),
            MessageType.ChatBroadcast => new ChatBroadcastMessage(ReadString(reader), ReadString(reader)),
            MessageType.ServerError => new ServerErrorMessage(ReadString(reader), ReadString(reader)),
            MessageType.EntitySpawn => new EntitySpawnMessage(
                reader.ReadUInt32(),
                ReadGuid(reader),
                (EntityKind)reader.ReadByte(),
                ReadString(reader),
                ReadTile(reader),
                ReadDirection(reader)),
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
            WriteSnapshotTileCoordinate(writer, entity.Tile.X);
            WriteSnapshotTileCoordinate(writer, entity.Tile.Y);
            writer.Write((byte)entity.Facing);
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
            var x = reader.ReadInt16();
            var y = reader.ReadInt16();
            var facing = ReadDirection(reader);
            entities.Add(new EntityStateSnapshot(networkId, new TileCoord(x, y), facing));
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

    private static void WriteSnapshotTileCoordinate(BinaryWriter writer, int value)
    {
        if (value < short.MinValue || value > short.MaxValue)
        {
            throw new ProtocolException($"Snapshot tile coordinate is out of range: {value}.");
        }

        writer.Write((short)value);
    }

    private static void WriteTile(BinaryWriter writer, TileCoord value)
    {
        WriteSnapshotTileCoordinate(writer, value.X);
        WriteSnapshotTileCoordinate(writer, value.Y);
    }

    private static TileCoord ReadTile(BinaryReader reader)
    {
        return new TileCoord(reader.ReadInt16(), reader.ReadInt16());
    }

    private static Direction8 ReadDirection(BinaryReader reader)
    {
        var value = reader.ReadByte();
        if (value > (byte)Direction8.NW)
        {
            throw new ProtocolException($"Invalid Direction8 value: {value}.");
        }

        return (Direction8)value;
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
