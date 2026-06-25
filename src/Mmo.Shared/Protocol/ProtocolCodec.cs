using System.Text;
using Mmo.Shared.Domain;

namespace Mmo.Shared.Protocol;

public static class ProtocolCodec
{
    public const uint Magic = 0x314F4D4D;
    // COMBAT-S2A (v27): per-entity public HP (Health + MaxHealth, ushort each) added to EntityStateSnapshot for
    // the overhead HP bar. Server + client ship together.
    // COMBAT-S2B (v28): new client->server AttackMessage (own attack-seq + attack kind) on its own dedup cursor.
    // FREEAIM (v29): AttackMessage gains a quantized continuous AIM ANGLE (ushort, 0..65535 -> [0,2π)) — the
    // player→cursor world bearing the server resolves a geometric sector against. Server + client ship together.
    // SWING-COMMIT-FIX (v30): AttackMessage gains an AUTHORED TICK (uint) — the integer server tick the client
    // stamped the swing on — so the server roots the attacker's movement at the SAME logical tick the predictor
    // did (mirroring the NET3 authored-tick step commit), killing the swing-then-move rubberband under latency.
    // Server + client ship together.
    // COMBAT-TUNING (v31): new server->client CombatTuningMessage replicating the live combat feel-knobs (attack
    // cooldown ms, swing-root ms, sector half-angle deg, radius tiles, damage) so the client's wedge/predictor/
    // cooldown-viz match the server's authoritative resolution. Sent on login + on every combat.* tuning change.
    // Server + client ship together.
    // COMBAT-QOL (v32): new server->client DamageEventMessage (victim NetworkId + Amount damage + new Health),
    // AOI-gated to the victim's viewers, so the client floats a "-N" number over the entity. Cosmetic only; sent
    // unreliable. Server + client ship together.
    // LIVING-ENEMIES P2-POLISH (v33): new server->client MonsterTuningMessage replicating the per-monster-TYPE tuning
    // (one entry per named template — slime now) so the F1 "Monster" tab can list the types and show + edit the live
    // values. Sent on login + on every per-type tuning change. Server + client ship together.
    // LIVING-ENEMIES P3 (v34): the per-monster MonsterHomeMessage (keyed by the monster's network id) is REPLACED by
    // SpawnerMarkerMessage (keyed by a stable spawner id + an Active flag). The red tile now represents the PERSISTENT
    // spawner, so it survives the monster's death/respawn; it is sent Active=true on spawner AOI-entry and Active=false
    // on AOI-exit. Wire layout changed (uint SpawnerId + tile + bool), so the version bumps. Server + client ship together.
    // LOOT P4c (v35): the corpse loot WINDOW. Two new messages — client->server LootActionMessage (corpse net id +
    // LootActionKind {TakeItem/LootAll/Close} + a template key for TakeItem) and server->owner CorpseContentsMessage
    // (corpse net id + Open flag + a CorpseItem[] of {template key, quantity, rarity}). Opening the window reuses the
    // existing InteractRequest on a corpse. Corpse contents now replicate (eligibility-gated) where P4b kept them
    // server-side. Server + client ship together.
    // CONTINUOUS MIGRATION (v36): the ATOMIC continuous wire break (mutually undecodable with v35 — server + every
    // in-repo client flip together). Three changes: (1) the per-entity snapshot POSITION is now fixed-point Q12.4
    // CONTINUOUS (PositionEncoding.Encode/Decode, two signed shorts of sixteenths-of-a-tile) instead of the rounded
    // tile — same 4 bytes/entity, now sub-tile precise. (2) the WorldSnapshot header gains LastInputSeq (uint, after
    // RecipientStepSeq) — the highest per-input MoveIntent seq the server integrated for the recipient. (3) MoveIntent
    // is RESHAPED to the per-input continuous move {uint InputSeq, float DirX, float DirY, float DtSeconds}, and the
    // dead tile-step machinery (MoveInput / StepCommitRequest / StepCommitBatch / MovementMode) is DELETED (its tags
    // 8–11 left as gaps). Server integrates each fresh input by its dt on the receive path with anti-speedhack clamps.
    // CONTINUOUS MIGRATION (v37, Phase 4): ServerHello gains a trailing float BodyRadiusUnits — the server's
    // authoritative player body radius, replicated so the new client predictor collides against the SAME radius the
    // server integrates with (the determinism contract at walls). Intra-branch bump (no deployed clients); server +
    // every in-repo client flip together. Otherwise identical to v36.
    public const byte Version = 37;

    private const int MaxMonsterTypes = 256;

    private const int MaxStringBytes = 2048;
    private const int MaxSnapshotEntities = 4096;
    private const int MaxInventoryUpdateStacks = 1024;

    // LOOT P4c: a corpse holds a handful of rolled stacks; bound the decoded list so a malformed/hostile packet
    // can't allocate unboundedly (same defensive cap idea as the inventory-update bound).
    private const int MaxCorpseItems = 256;

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
                // CONTINUOUS MIGRATION (v36): per-input continuous move — InputSeq, raw DirX/DirY, DtSeconds.
                writer.Write(value.InputSeq);
                writer.Write(value.DirX);
                writer.Write(value.DirY);
                writer.Write(value.DtSeconds);
                break;
            case AttackMessage value:
                writer.Write(value.Sequence);
                writer.Write((byte)value.Kind);
                // FREEAIM (v29): quantized continuous aim angle, after the kind. Mirrored in the Attack decode.
                writer.Write(value.AimAngle);
                // SWING-COMMIT-FIX (v30): authored tick last, so the server can root the swing at the same logical
                // tick the predictor did. Mirrored in the Attack decode (read in the same order).
                writer.Write(value.AuthoredTick);
                break;
            case LootActionMessage value:
                writer.Write(value.CorpseNetworkId);
                writer.Write((byte)value.Kind);
                WriteString(writer, value.TemplateKey);
                break;
            case ChatSendMessage value:
                WriteString(writer, value.Text);
                break;
            case AdminSetStatMessage value:
                writer.Write(value.Stat);
                writer.Write(value.Value);
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
                // CONTINUOUS MIGRATION (v37): replicate the authoritative body radius (mirrored in the decode below).
                writer.Write(value.BodyRadiusUnits);
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
                    value.LastInputSeq,
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
            case PlayerStatsMessage value:
                WriteCharacterStats(writer, value.Stats);
                break;
            case CombatTuningMessage value:
                WriteCombatTuning(writer, value.Tuning);
                break;
            case DamageEventMessage value:
                writer.Write(value.NetworkId);
                writer.Write(value.Amount);
                writer.Write(value.Health);
                break;
            case MonsterTuningMessage value:
                WriteMonsterTuning(writer, value.Tuning);
                break;
            case SpawnerMarkerMessage value:
                writer.Write(value.SpawnerId);
                WriteTile(writer, value.Tile);
                writer.Write(value.Active);
                break;
            case CorpseContentsMessage value:
                WriteCorpseContents(writer, value);
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
        uint lastInputSeq,
        int totalEntities,
        bool isComplete,
        int chunkIndex,
        int chunkCount,
        IReadOnlyList<EntityStateSnapshot> entities)
    {
        WriteHeader(writer, MessageType.WorldSnapshot);
        WriteWorldSnapshotPayload(writer, serverTick, snapshotSequence, recipientStepSeq, lastInputSeq, totalEntities, isComplete, chunkIndex, chunkCount, entities);
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
            // CONTINUOUS MIGRATION (v36): per-input continuous move — InputSeq, raw DirX/DirY, DtSeconds.
            MessageType.MoveIntent => new MoveIntentMessage(reader.ReadUInt32(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
            MessageType.Attack => new AttackMessage(reader.ReadUInt32(), ReadAttackKind(reader), reader.ReadUInt16(), reader.ReadUInt32()),
            MessageType.LootAction => new LootActionMessage(reader.ReadUInt32(), ReadLootActionKind(reader), ReadString(reader)),
            MessageType.ChatSend => new ChatSendMessage(ReadString(reader)),
            MessageType.AdminSetStat => new AdminSetStatMessage(reader.ReadByte(), reader.ReadInt32()),
            MessageType.AdminSetTuning => new AdminSetTuningMessage(ReadString(reader), reader.ReadDouble()),
            MessageType.SnapshotAck => new SnapshotAckMessage(reader.ReadUInt32()),
            MessageType.InteractRequest => new InteractRequestMessage(reader.ReadUInt32()),
            MessageType.InteractResult => new InteractResultMessage(reader.ReadBoolean(), ReadString(reader)),
            MessageType.InventoryUpdate => ReadInventoryUpdate(reader),
            // CONTINUOUS MIGRATION (v37): trailing BodyRadiusUnits float mirrors the write order.
            MessageType.ServerHello => new ServerHelloMessage(ReadString(reader), reader.ReadByte(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadSingle(), reader.ReadSingle()),
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
            MessageType.PlayerStats => new PlayerStatsMessage(ReadCharacterStats(reader)),
            MessageType.CombatTuning => new CombatTuningMessage(ReadCombatTuning(reader)),
            MessageType.DamageEvent => new DamageEventMessage(reader.ReadUInt32(), reader.ReadInt32(), reader.ReadUInt16()),
            MessageType.MonsterTuning => new MonsterTuningMessage(ReadMonsterTuning(reader)),
            MessageType.SpawnerMarker => new SpawnerMarkerMessage(reader.ReadUInt32(), ReadTile(reader), reader.ReadBoolean()),
            MessageType.CorpseContents => ReadCorpseContents(reader),
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
            // CONTINUOUS MIGRATION (v36): the snapshot position is now CONTINUOUS — fixed-point Q12.4 (two signed
            // shorts of sixteenths-of-a-tile via the shared PositionEncoding), so the wire carries the sub-tile
            // position (was the rounded tile in v35). Same 4 bytes/entity. Quantize ON SEND ONLY — the server's
            // authoritative double Position is never rounded by this.
            var (qx, qy) = PositionEncoding.Encode(entity.Position);
            writer.Write(qx);
            writer.Write(qy);
            writer.Write((byte)entity.Facing);
            writer.Write(entity.Depleted);
            // COMBAT-S2A (v27): public HP rides each per-entity state, after Depleted. MaxHealth == 0 means
            // "no HP" (resources/players-without-stats); the client hides the bar in that case.
            writer.Write(entity.Health);
            writer.Write(entity.MaxHealth);
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
        uint lastInputSeq,
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
        // CONTINUOUS MIGRATION (v36): the recipient-scoped last INTEGRATED input seq rides right after
        // RecipientStepSeq (mirrored in ReadWorldSnapshot). Phase-4 predictor trims/replays its input buffer to it.
        writer.Write(lastInputSeq);
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
        // CONTINUOUS MIGRATION (v36): mirrors the write order — last integrated input seq right after RecipientStepSeq.
        var lastInputSeq = reader.ReadUInt32();
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

        return new WorldSnapshotMessage(tick, sequence, totalEntities, isComplete, chunkIndex, chunkCount, entities, recipientStepSeq, lastInputSeq);
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
            // CONTINUOUS MIGRATION (v36): the two shorts are now fixed-point Q12.4 (sixteenths of a tile), decoded
            // back to the continuous WorldVector via the shared PositionEncoding (mirrors WriteEntityStates).
            var qx = reader.ReadInt16();
            var qy = reader.ReadInt16();
            var position = PositionEncoding.Decode(qx, qy);
            var facing = ReadDirection(reader);
            var depleted = reader.ReadBoolean();
            // COMBAT-S2A (v27): mirrors WriteEntityStates — Health then MaxHealth, ushort each.
            var health = reader.ReadUInt16();
            var maxHealth = reader.ReadUInt16();
            entities.Add(new EntityStateSnapshot(networkId, position, facing, depleted, health, maxHealth));
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

    // COMBAT-S1: the six vital ints (current+max for HP/mana/stamina) in a fixed order. Mirrored in
    // ReadCharacterStats. Ints (not packed) — vitals ride an owner-only, rarely-sent reliable message, so the
    // few extra bytes are irrelevant and headroom is free.
    private static void WriteCharacterStats(BinaryWriter writer, CharacterStats stats)
    {
        writer.Write(stats.Health);
        writer.Write(stats.MaxHealth);
        writer.Write(stats.Mana);
        writer.Write(stats.MaxMana);
        writer.Write(stats.Stamina);
        writer.Write(stats.MaxStamina);
    }

    // COMBAT-TUNING (v31): the five combat feel-knobs in a fixed order. Mirrored in ReadCombatTuning. Ints for the
    // ms/damage knobs, doubles for the geometry (half-angle deg, radius tiles) so the panel can nudge fractional
    // reach/arc. Rides an owner/all-clients reliable message sent rarely — the few extra bytes are irrelevant.
    private static void WriteCombatTuning(BinaryWriter writer, CombatTuningSnapshot tuning)
    {
        writer.Write(tuning.AttackCooldownMs);
        writer.Write(tuning.RootMs);
        writer.Write(tuning.HalfAngleDegrees);
        writer.Write(tuning.RadiusTiles);
        writer.Write(tuning.Damage);
    }

    // LIVING-ENEMIES P2-POLISH (v33): the per-monster-TYPE tuning — a count-prefixed list of per-type entries, each
    // its stable id + display name + the ms/tile feel-values in a fixed order. Mirrored in ReadMonsterTuning. Rides a
    // rare reliable all-clients message (login + on change), so the bytes are irrelevant.
    private static void WriteMonsterTuning(BinaryWriter writer, MonsterTuningSnapshot tuning)
    {
        var types = tuning.Types;
        if (types.Count > MaxMonsterTypes)
        {
            throw new ProtocolException($"Monster tuning has too many types: {types.Count}.");
        }

        writer.Write((ushort)types.Count);
        foreach (var t in types)
        {
            WriteString(writer, t.Id);
            WriteString(writer, t.DisplayName);
            writer.Write(t.MaxHealth);
            writer.Write(t.MoveSpeedMultiplier);
            writer.Write(t.RoamRadius);
            writer.Write(t.PauseMinMs);
            writer.Write(t.PauseMaxMs);
            writer.Write(t.AggroRadius);
            writer.Write(t.ChaseLeash);
            writer.Write(t.AttackRange);
            writer.Write(t.AttackDamage);
            writer.Write(t.AttackCooldownMs);
            writer.Write(t.RespawnMs);
        }
    }

    private static MonsterTuningSnapshot ReadMonsterTuning(BinaryReader reader)
    {
        var count = reader.ReadUInt16();
        if (count > MaxMonsterTypes)
        {
            throw new ProtocolException($"Monster tuning has too many types: {count}.");
        }

        var types = new List<MonsterTypeSnapshot>(count);
        for (var i = 0; i < count; i++)
        {
            var id = ReadString(reader);
            var displayName = ReadString(reader);
            var maxHealth = reader.ReadInt32();
            var moveSpeed = reader.ReadDouble();
            var roamRadius = reader.ReadInt32();
            var pauseMinMs = reader.ReadInt32();
            var pauseMaxMs = reader.ReadInt32();
            var aggroRadius = reader.ReadInt32();
            var chaseLeash = reader.ReadInt32();
            var attackRange = reader.ReadInt32();
            var attackDamage = reader.ReadInt32();
            var attackCooldownMs = reader.ReadInt32();
            var respawnMs = reader.ReadInt32();
            types.Add(new MonsterTypeSnapshot(
                id, displayName, maxHealth, moveSpeed, roamRadius, pauseMinMs, pauseMaxMs,
                aggroRadius, chaseLeash, attackRange, attackDamage, attackCooldownMs, respawnMs));
        }

        return new MonsterTuningSnapshot(types);
    }

    private static CombatTuningSnapshot ReadCombatTuning(BinaryReader reader)
    {
        var attackCooldownMs = reader.ReadInt32();
        var rootMs = reader.ReadInt32();
        var halfAngleDeg = reader.ReadDouble();
        var radiusTiles = reader.ReadDouble();
        var damage = reader.ReadInt32();
        return new CombatTuningSnapshot(attackCooldownMs, rootMs, halfAngleDeg, radiusTiles, damage);
    }

    private static CharacterStats ReadCharacterStats(BinaryReader reader)
    {
        var health = reader.ReadInt32();
        var maxHealth = reader.ReadInt32();
        var mana = reader.ReadInt32();
        var maxMana = reader.ReadInt32();
        var stamina = reader.ReadInt32();
        var maxStamina = reader.ReadInt32();
        return new CharacterStats(health, maxHealth, mana, maxMana, stamina, maxStamina);
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

    // COMBAT-S2B: validate the attack-kind byte against the known set so a malformed/hostile packet can't
    // smuggle an out-of-range kind into the server handler. Only MeleeCone exists this stage.
    private static AttackKind ReadAttackKind(BinaryReader reader)
    {
        var value = reader.ReadByte();
        if (value > (byte)AttackKind.MeleeCone)
        {
            throw new ProtocolException($"Invalid AttackKind value: {value}.");
        }

        return (AttackKind)value;
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

    // LOOT P4c: a LootActionKind is one wire byte; an out-of-range value is a protocol error (not a silent default,
    // mirroring ReadAttackKind) so a corrupt/hostile packet can't be quietly misinterpreted as e.g. LootAll.
    private static LootActionKind ReadLootActionKind(BinaryReader reader)
    {
        var raw = reader.ReadByte();
        if (raw > (byte)LootActionKind.Close)
        {
            throw new ProtocolException($"Unknown loot action kind {raw}.");
        }

        return (LootActionKind)raw;
    }

    // LOOT P4c: wire layout = corpse network id (uint), Open (bool), then a ushort count + that many
    // {TemplateKey (string), Quantity (int >= 0), Rarity (byte)} rows. Mirrored in ReadCorpseContents.
    private static void WriteCorpseContents(BinaryWriter writer, CorpseContentsMessage value)
    {
        if (value.Items.Count > MaxCorpseItems)
        {
            throw new ProtocolException($"Corpse contents have too many items: {value.Items.Count}.");
        }

        writer.Write(value.CorpseNetworkId);
        writer.Write(value.Open);
        writer.Write((ushort)value.Items.Count);
        foreach (var item in value.Items)
        {
            WriteString(writer, item.TemplateKey);
            if (item.Quantity < 0)
            {
                throw new ProtocolException($"Corpse item quantity is negative: {item.Quantity}.");
            }

            writer.Write(item.Quantity);
            writer.Write((byte)item.Rarity);
        }
    }

    private static CorpseContentsMessage ReadCorpseContents(BinaryReader reader)
    {
        var corpseNetworkId = reader.ReadUInt32();
        var open = reader.ReadBoolean();
        var count = reader.ReadUInt16();
        if (count > MaxCorpseItems)
        {
            throw new ProtocolException($"Corpse contents have too many items: {count}.");
        }

        var items = new List<CorpseItem>(count);
        for (var i = 0; i < count; i++)
        {
            var key = ReadString(reader);
            var quantity = reader.ReadInt32();
            if (quantity < 0)
            {
                throw new ProtocolException($"Corpse item quantity is negative: {quantity}.");
            }

            var rarity = (Rarity)reader.ReadByte();
            items.Add(new CorpseItem(key, quantity, rarity));
        }

        return new CorpseContentsMessage(corpseNetworkId, open, items);
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
