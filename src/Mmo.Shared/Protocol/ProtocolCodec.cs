using System.Text;
using Mmo.Shared.Domain;

namespace Mmo.Shared.Protocol;

public static class ProtocolCodec
{
    public const uint Magic = 0x314F4D4D;
    public const byte Version = 22;

    private const int MaxStringBytes = 2048;
    private const int MaxSnapshotEntities = 4096;
    private const int MaxInventoryUpdateStacks = 1024;

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
        WriteHeader(writer, message.Type);

        switch (message)
        {
            case ClientHelloMessage value:
                WriteString(writer, value.ClientName);
                break;
            case LoginRequestMessage value:
                WriteString(writer, value.AccountName);
                WriteString(writer, value.DisplayName);
                break;
            case MoveIntentMessage value:
                writer.Write(value.Sequence);
                writer.Write(value.Moving);
                writer.Write((byte)value.Direction);
                break;
            case StepCommitRequestMessage value:
                writer.Write(value.Sequence);
                writer.Write((byte)value.Direction);
                break;
            case MovementModeMessage value:
                writer.Write(value.ClientDriven);
                break;
            case ChatSendMessage value:
                WriteString(writer, value.Text);
                break;
            case AdminSetTuningMessage value:
                WriteString(writer, value.Key);
                writer.Write(value.Value);
                break;
            case SnapshotAckMessage value:
                writer.Write(value.LastSnapshotSequence);
                break;
            case InteractRequestMessage value:
                writer.Write(value.TargetNetworkId);
                break;
            case InteractResultMessage value:
                writer.Write(value.Success);
                WriteString(writer, value.Reason);
                break;
            case InventoryUpdateMessage value:
                WriteInventoryUpdate(writer, value.ChangedStacks);
                break;
            case ServerHelloMessage value:
                WriteString(writer, value.ServerName);
                writer.Write(value.ProtocolVersion);
                writer.Write(value.TickRate);
                writer.Write(value.StepCooldownMs);
                writer.Write(value.InterestRadiusTiles);
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
                WriteWorldSnapshotPayload(
                    writer,
                    value.ServerTick,
                    value.SnapshotSequence,
                    value.RecipientStepSeq,
                    value.TotalEntities,
                    value.IsComplete,
                    value.ChunkIndex,
                    value.ChunkCount,
                    value.Entities);
                break;
            case EntitySpawnMessage value:
                writer.Write(value.NetworkId);
                WriteGuid(writer, value.CharacterId);
                writer.Write((byte)value.Kind);
                WriteString(writer, value.DisplayName);
                WriteTile(writer, value.Tile);
                writer.Write((byte)value.Facing);
                writer.Write(value.StepCooldownMs);
                break;
            case MovementSpeedChangedMessage value:
                writer.Write(value.NetworkId);
                writer.Write(value.StepCooldownMs);
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

    public static void EncodeWorldSnapshot(
        BinaryWriter writer,
        uint serverTick,
        uint snapshotSequence,
        uint recipientStepSeq,
        int totalEntities,
        bool isComplete,
        int chunkIndex,
        int chunkCount,
        IReadOnlyList<EntityStateSnapshot> entities)
    {
        WriteHeader(writer, MessageType.WorldSnapshot);
        WriteWorldSnapshotPayload(writer, serverTick, snapshotSequence, recipientStepSeq, totalEntities, isComplete, chunkIndex, chunkCount, entities);
    }

    public static void EncodeEntitySpawn(
        BinaryWriter writer,
        uint networkId,
        Guid characterId,
        EntityKind kind,
        string displayName,
        TileCoord tile,
        Direction8 facing,
        ushort stepCooldownMs)
    {
        WriteHeader(writer, MessageType.EntitySpawn);
        writer.Write(networkId);
        WriteGuid(writer, characterId);
        writer.Write((byte)kind);
        WriteString(writer, displayName);
        WriteTile(writer, tile);
        writer.Write((byte)facing);
        writer.Write(stepCooldownMs);
    }

    public static void EncodeEntityDespawn(BinaryWriter writer, uint serverTick, uint networkId)
    {
        WriteHeader(writer, MessageType.EntityDespawn);
        writer.Write(serverTick);
        writer.Write(networkId);
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
            MessageType.MoveIntent => new MoveIntentMessage(reader.ReadUInt32(), reader.ReadBoolean(), ReadDirection(reader)),
            MessageType.StepCommitRequest => new StepCommitRequestMessage(reader.ReadUInt32(), ReadDirection(reader)),
            MessageType.MovementMode => new MovementModeMessage(reader.ReadBoolean()),
            MessageType.ChatSend => new ChatSendMessage(ReadString(reader)),
            MessageType.AdminSetTuning => new AdminSetTuningMessage(ReadString(reader), reader.ReadDouble()),
            MessageType.SnapshotAck => new SnapshotAckMessage(reader.ReadUInt32()),
            MessageType.InteractRequest => new InteractRequestMessage(reader.ReadUInt32()),
            MessageType.InteractResult => new InteractResultMessage(reader.ReadBoolean(), ReadString(reader)),
            MessageType.InventoryUpdate => ReadInventoryUpdate(reader),
            MessageType.ServerHello => new ServerHelloMessage(ReadString(reader), reader.ReadByte(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadSingle()),
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
                ReadDirection(reader),
                reader.ReadUInt16()),
            MessageType.MovementSpeedChanged => new MovementSpeedChangedMessage(reader.ReadUInt32(), reader.ReadUInt16()),
            MessageType.EntityDespawn => new EntityDespawnMessage(reader.ReadUInt32(), reader.ReadUInt32()),
            MessageType.ZoneInfo => ReadZoneInfo(reader),
            _ => throw new ProtocolException($"Unknown message type {(ushort)type}.")
        };
    }

    // Terrain ships as a seed descriptor, not a tile payload: dims + (Seed, GenVersion) + ContentHash.
    // Fixed-size and tiny — login terrain cost is constant regardless of map size. The client
    // regenerates the map locally via the shared TerrainGenerator and validates ContentHash.
    private static void WriteZoneInfo(BinaryWriter writer, ZoneInfoMessage zone)
    {
        WriteString(writer, zone.ZoneId);
        WriteZoneDimension(writer, zone.Width, nameof(zone.Width));
        WriteZoneDimension(writer, zone.Height, nameof(zone.Height));
        writer.Write(zone.Seed);
        writer.Write(zone.GenVersion);
        writer.Write(zone.ContentHash);
    }

    private static ZoneInfoMessage ReadZoneInfo(BinaryReader reader)
    {
        var zoneId = ReadString(reader);
        var width = reader.ReadUInt16();
        var height = reader.ReadUInt16();
        var seed = reader.ReadInt32();
        var genVersion = reader.ReadInt32();
        var contentHash = reader.ReadUInt64();
        return new ZoneInfoMessage(zoneId, width, height, seed, genVersion, contentHash);
    }

    private static void WriteZoneDimension(BinaryWriter writer, int value, string name)
    {
        if (value < 1 || value > ushort.MaxValue)
        {
            throw new ProtocolException($"Invalid zone {name}: {value}.");
        }

        writer.Write((ushort)value);
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
            writer.Write(entity.Depleted);
        }
    }

    private static void WriteHeader(BinaryWriter writer, MessageType type)
    {
        writer.Write(Magic);
        writer.Write(Version);
        writer.Write((ushort)type);
    }

    private static void WriteWorldSnapshotPayload(
        BinaryWriter writer,
        uint serverTick,
        uint snapshotSequence,
        uint recipientStepSeq,
        int totalEntities,
        bool isComplete,
        int chunkIndex,
        int chunkCount,
        IReadOnlyList<EntityStateSnapshot> entities)
    {
        writer.Write(serverTick);
        writer.Write(snapshotSequence);
        // S76 (v19): recipient-scoped step sequence rides the header, immediately after SnapshotSequence and
        // before the chunk/entity metadata. Mirrored at the same position in ReadWorldSnapshot.
        writer.Write(recipientStepSeq);
        WriteSnapshotMetadata(writer, totalEntities, isComplete, chunkIndex, chunkCount, entities.Count);
        WriteEntityStates(writer, entities);
    }

    private static void WriteSnapshotMetadata(BinaryWriter writer, WorldSnapshotMessage snapshot)
    {
        WriteSnapshotMetadata(
            writer,
            snapshot.TotalEntities,
            snapshot.IsComplete,
            snapshot.ChunkIndex,
            snapshot.ChunkCount,
            snapshot.Entities.Count);
    }

    private static void WriteSnapshotMetadata(
        BinaryWriter writer,
        int totalEntities,
        bool isComplete,
        int chunkIndex,
        int chunkCount,
        int entityCount)
    {
        if (totalEntities < entityCount || totalEntities > MaxSnapshotEntities)
        {
            throw new ProtocolException($"Invalid snapshot total entity count: {totalEntities}.");
        }

        if (chunkCount < 1 || chunkIndex < 0 || chunkIndex >= chunkCount)
        {
            throw new ProtocolException($"Invalid snapshot chunk {chunkIndex}/{chunkCount}.");
        }

        writer.Write((ushort)totalEntities);
        writer.Write(isComplete);
        writer.Write((ushort)chunkIndex);
        writer.Write((ushort)chunkCount);
    }

    private static WorldSnapshotMessage ReadWorldSnapshot(BinaryReader reader)
    {
        var tick = reader.ReadUInt32();
        var sequence = reader.ReadUInt32();
        // S76 (v19): mirrors the write order — recipient step seq immediately after SnapshotSequence.
        var recipientStepSeq = reader.ReadUInt32();
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

        return new WorldSnapshotMessage(tick, sequence, totalEntities, isComplete, chunkIndex, chunkCount, entities, recipientStepSeq);
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
            var depleted = reader.ReadBoolean();
            entities.Add(new EntityStateSnapshot(networkId, new TileCoord(x, y), facing, depleted));
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

    private static void WriteInventoryUpdate(BinaryWriter writer, IReadOnlyList<ItemStack> stacks)
    {
        if (stacks.Count > MaxInventoryUpdateStacks)
        {
            throw new ProtocolException($"Inventory update has too many stacks: {stacks.Count}.");
        }

        writer.Write((ushort)stacks.Count);
        foreach (var stack in stacks)
        {
            WriteString(writer, stack.TemplateKey);
            if (stack.Quantity < 0)
            {
                throw new ProtocolException($"Inventory stack quantity is negative: {stack.Quantity}.");
            }

            writer.Write(stack.Quantity);
        }
    }

    private static InventoryUpdateMessage ReadInventoryUpdate(BinaryReader reader)
    {
        var count = reader.ReadUInt16();
        if (count > MaxInventoryUpdateStacks)
        {
            throw new ProtocolException($"Inventory update has too many stacks: {count}.");
        }

        var stacks = new List<ItemStack>(count);
        for (var i = 0; i < count; i++)
        {
            var key = ReadString(reader);
            var quantity = reader.ReadInt32();
            if (quantity < 0)
            {
                throw new ProtocolException($"Inventory stack quantity is negative: {quantity}.");
            }

            stacks.Add(new ItemStack(key, quantity));
        }

        return new InventoryUpdateMessage(stacks);
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
