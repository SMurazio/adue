using System.Text;
using Mmo.Shared.Domain;

namespace Mmo.Shared.Protocol;

public static class ProtocolCodec
{
    public const uint Magic = 0x314F4D4D;
    public const byte Version = 11;

    private const int MaxStringBytes = 2048;
    private const int MaxSnapshotEntities = 4096;
    private const int MaxZoneTiles = 1_048_576;

    public static byte[] Encode(IProtocolMessage message)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);

        Encode(message, writer);
        writer.Flush();
        return stream.ToArray();
    }

    public static void Encode(IProtocolMessage message, BinaryWriter writer)
    {
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
                writer.Write(value.StepCooldownMs);
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
            case ZoneInfoMessage value:
                WriteZoneInfo(writer, value);
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
            MessageType.ServerHello => new ServerHelloMessage(ReadString(reader), reader.ReadByte(), reader.ReadInt32(), reader.ReadInt32()),
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
            MessageType.ZoneInfo => ReadZoneInfo(reader),
            _ => throw new ProtocolException($"Unknown message type {(ushort)type}.")
        };
    }

    private static void WriteZoneInfo(BinaryWriter writer, ZoneInfoMessage zone)
    {
        WriteString(writer, zone.ZoneId);
        WriteZoneDimension(writer, zone.Width, nameof(zone.Width));
        WriteZoneDimension(writer, zone.Height, nameof(zone.Height));
        var tileCount = CheckedZoneTileCount(zone.Width, zone.Height);
        var bitset = new byte[(tileCount + 7) / 8];

        foreach (var tile in zone.BlockedTiles)
        {
            if (tile.X < 0 || tile.X >= zone.Width || tile.Y < 0 || tile.Y >= zone.Height)
            {
                throw new ProtocolException($"Blocked tile is out of zone bounds: {tile}.");
            }

            var index = (tile.Y * zone.Width) + tile.X;
            bitset[index / 8] |= (byte)(1 << (index % 8));
        }

        writer.Write(bitset.Length);
        writer.Write(bitset);
    }

    private static ZoneInfoMessage ReadZoneInfo(BinaryReader reader)
    {
        var zoneId = ReadString(reader);
        var width = reader.ReadUInt16();
        var height = reader.ReadUInt16();
        var tileCount = CheckedZoneTileCount(width, height);
        var expectedBytes = (tileCount + 7) / 8;
        var byteCount = reader.ReadInt32();
        if (byteCount != expectedBytes)
        {
            throw new ProtocolException($"Invalid zone bitset size: {byteCount}, expected {expectedBytes}.");
        }

        var bitset = reader.ReadBytes(byteCount);
        if (bitset.Length != byteCount)
        {
            throw new ProtocolException("Zone bitset payload ended early.");
        }

        var blocked = new List<TileCoord>();
        for (var index = 0; index < tileCount; index++)
        {
            if ((bitset[index / 8] & (1 << (index % 8))) == 0)
            {
                continue;
            }

            blocked.Add(new TileCoord(index % width, index / width));
        }

        return new ZoneInfoMessage(zoneId, width, height, blocked);
    }

    private static void WriteZoneDimension(BinaryWriter writer, int value, string name)
    {
        if (value < 1 || value > ushort.MaxValue)
        {
            throw new ProtocolException($"Invalid zone {name}: {value}.");
        }

        writer.Write((ushort)value);
    }

    private static int CheckedZoneTileCount(int width, int height)
    {
        var tileCount = (long)width * height;
        if (tileCount < 1 || tileCount > MaxZoneTiles)
        {
            throw new ProtocolException($"Zone is too large to encode: {width}x{height}.");
        }

        return (int)tileCount;
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
