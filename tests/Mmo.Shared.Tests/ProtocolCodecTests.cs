using Mmo.Shared.Domain;
using Mmo.Shared.Protocol;
using System.Text;
using Xunit;

namespace Mmo.Shared.Tests;

public sealed class ProtocolCodecTests
{
    [Fact]
    public void LoginRequestRoundTrips()
    {
        var original = new LoginRequestMessage("account", "display");

        var decoded = ProtocolCodec.Decode(ProtocolCodec.Encode(original));

        Assert.Equal(original, decoded);
    }

    [Fact]
    public void WorldSnapshotRoundTrips()
    {
        var original = new WorldSnapshotMessage(
            42,
            77,
            120,
            false,
            2,
            6,
            new[]
            {
                new EntityStateSnapshot(99, WorldVector.FromTile(12, -25), Direction8.NE)
            },
            RecipientStepSeq: 555,
            LastInputSeq: 909);

        var decoded = Assert.IsType<WorldSnapshotMessage>(ProtocolCodec.Decode(ProtocolCodec.Encode(original)));

        Assert.Equal(original.ServerTick, decoded.ServerTick);
        Assert.Equal(77u, decoded.SnapshotSequence);
        Assert.Equal(555u, decoded.RecipientStepSeq);
        // CONTINUOUS MIGRATION (v36): the LastInputSeq header field round-trips after RecipientStepSeq.
        Assert.Equal(909u, decoded.LastInputSeq);
        Assert.Equal(120, decoded.TotalEntities);
        Assert.False(decoded.IsComplete);
        Assert.Equal(2, decoded.ChunkIndex);
        Assert.Equal(6, decoded.ChunkCount);
        var entity = Assert.Single(decoded.Entities);
        Assert.Equal(99u, entity.NetworkId);
        // CONTINUOUS MIGRATION (v36): the wire now carries the CONTINUOUS fixed-point position. A tile-centre encodes
        // losslessly, so this exact-integer position round-trips byte-for-byte.
        Assert.Equal(WorldVector.FromTile(12, -25), entity.Position);
        Assert.Equal(Direction8.NE, entity.Facing);
    }

    // CONTINUOUS MIGRATION (v36): a genuinely FRACTIONAL position round-trips within the fixed-point quantum (1/16
    // tile = 0.0625 u) — the Q12.4 wire precision the migration locked in.
    [Fact]
    public void WorldSnapshotContinuousPositionRoundTripsWithinOneSixteenth()
    {
        var original = new WorldSnapshotMessage(
            1,
            1,
            new[]
            {
                new EntityStateSnapshot(7, new WorldVector(3.3333, -8.77), Direction8.S)
            });

        var decoded = Assert.IsType<WorldSnapshotMessage>(ProtocolCodec.Decode(ProtocolCodec.Encode(original)));

        var entity = Assert.Single(decoded.Entities);
        Assert.True(System.Math.Abs(entity.Position.X - 3.3333) <= 1d / 16d);
        Assert.True(System.Math.Abs(entity.Position.Y - (-8.77)) <= 1d / 16d);
    }

    // CONTINUOUS MIGRATION (v36): a v35 packet (the old version byte) is no longer decodable — the atomic break is
    // mutually undecodable. The codec rejects any version != ProtocolCodec.Version.
    [Fact]
    public void V35PacketFailsToDecode()
    {
        var bytes = ProtocolCodec.Encode(new MoveIntentMessage(1, 1f, 0f, 0.05f));
        // The version byte rides immediately after the 4-byte magic (little-endian uint).
        bytes[4] = 35;
        Assert.Throws<ProtocolException>(() => ProtocolCodec.Decode(bytes));
    }

    // LIVING-ENEMIES P2-POLISH (v33): the per-monster-TYPE tuning snapshot round-trips (count-prefixed list of
    // per-type entries, each id + display name + the ms/tile values).
    [Fact]
    public void MonsterTuningRoundTrips()
    {
        var original = new MonsterTuningMessage(new MonsterTuningSnapshot(new[]
        {
            new MonsterTypeSnapshot("slime", "Slime", 100, 0.8, 4, 2000, 5000, 6, 12, 1, 10, 1000, 5000),
            new MonsterTypeSnapshot("ogre", "Ogre", 250, 0.6, 3, 1000, 4000, 8, 20, 1, 25, 1500, 8000),
        }));

        var decoded = Assert.IsType<MonsterTuningMessage>(ProtocolCodec.Decode(ProtocolCodec.Encode(original)));

        Assert.Equal(2, decoded.Tuning.Types.Count);
        Assert.Equal("slime", decoded.Tuning.Types[0].Id);
        Assert.Equal("Slime", decoded.Tuning.Types[0].DisplayName);
        Assert.Equal(0.8, decoded.Tuning.Types[0].MoveSpeedMultiplier, 6);
        Assert.Equal(100, decoded.Tuning.Types[0].MaxHealth);
        Assert.Equal("ogre", decoded.Tuning.Types[1].Id);
        Assert.Equal(250, decoded.Tuning.Types[1].MaxHealth);
        Assert.Equal(25, decoded.Tuning.Types[1].AttackDamage);
        Assert.Equal(5000, decoded.Tuning.Types[0].RespawnMs);
        Assert.Equal(8000, decoded.Tuning.Types[1].RespawnMs);
    }

    // LIVING-ENEMIES P3 (v34): the persistent spawner red-tile marker round-trips (spawner id + tile + active flag).
    [Fact]
    public void SpawnerMarkerRoundTrips()
    {
        var original = new SpawnerMarkerMessage(4242, new TileCoord(13, -7), true);

        var decoded = Assert.IsType<SpawnerMarkerMessage>(ProtocolCodec.Decode(ProtocolCodec.Encode(original)));

        Assert.Equal(4242u, decoded.SpawnerId);
        Assert.Equal(new TileCoord(13, -7), decoded.Tile);
        Assert.True(decoded.Active);

        var inactive = new SpawnerMarkerMessage(7, default, false);
        var decodedInactive = Assert.IsType<SpawnerMarkerMessage>(ProtocolCodec.Decode(ProtocolCodec.Encode(inactive)));
        Assert.Equal(7u, decodedInactive.SpawnerId);
        Assert.False(decodedInactive.Active);
    }

    [Fact]
    public void InteractRequestRoundTrips()
    {
        var original = new InteractRequestMessage(4242);

        var decoded = Assert.IsType<InteractRequestMessage>(ProtocolCodec.Decode(ProtocolCodec.Encode(original)));

        Assert.Equal(4242u, decoded.TargetNetworkId);
    }

    [Fact]
    public void InteractResultRoundTripsSuccessAndFailure()
    {
        var success = Assert.IsType<InteractResultMessage>(
            ProtocolCodec.Decode(ProtocolCodec.Encode(new InteractResultMessage(true, ""))));
        Assert.True(success.Success);
        Assert.Equal("", success.Reason);

        var failure = Assert.IsType<InteractResultMessage>(
            ProtocolCodec.Decode(ProtocolCodec.Encode(new InteractResultMessage(false, "too_far"))));
        Assert.False(failure.Success);
        Assert.Equal("too_far", failure.Reason);
    }

    [Fact]
    public void InventoryUpdateRoundTrips()
    {
        var original = new InventoryUpdateMessage(
        [
            new ItemStack("wood", 5),
            new ItemStack("stone", 0),
        ]);

        var decoded = Assert.IsType<InventoryUpdateMessage>(ProtocolCodec.Decode(ProtocolCodec.Encode(original)));

        Assert.Equal(2, decoded.ChangedStacks.Count);
        Assert.Equal(new ItemStack("wood", 5), decoded.ChangedStacks[0]);
        Assert.Equal(new ItemStack("stone", 0), decoded.ChangedStacks[1]);
    }

    [Fact]
    public void WorldSnapshotRoundTripsHealthFields()
    {
        // COMBAT-S2A: public HP (current + max) rides each per-entity state. A stat-bearing entity carries a
        // partial HP (the dummy/player bar); a stat-less entity (resource) carries 0/0 ("no HP").
        var original = new WorldSnapshotMessage(
            21,
            [
                new EntityStateSnapshot(31, WorldVector.FromTile(8, 9), Direction8.S, Depleted: false, Health: 70, MaxHealth: 100),
                new EntityStateSnapshot(32, WorldVector.FromTile(2, 3), Direction8.N, Depleted: false, Health: 0, MaxHealth: 0),
            ]);

        var decoded = Assert.IsType<WorldSnapshotMessage>(ProtocolCodec.Decode(ProtocolCodec.Encode(original)));

        Assert.Equal((ushort)70, decoded.Entities[0].Health);
        Assert.Equal((ushort)100, decoded.Entities[0].MaxHealth);
        Assert.True(decoded.Entities[0].MaxHealth > 0);
        Assert.Equal((ushort)0, decoded.Entities[1].Health);
        Assert.Equal((ushort)0, decoded.Entities[1].MaxHealth);
    }

    [Fact]
    public void WorldSnapshotRoundTripsDepletedFlag()
    {
        var original = new WorldSnapshotMessage(
            7,
            [
                new EntityStateSnapshot(11, WorldVector.FromTile(3, 4), Direction8.S, Depleted: true),
                new EntityStateSnapshot(12, WorldVector.FromTile(5, 6), Direction8.N, Depleted: false),
            ]);

        var decoded = Assert.IsType<WorldSnapshotMessage>(ProtocolCodec.Decode(ProtocolCodec.Encode(original)));

        Assert.True(decoded.Entities[0].Depleted);
        Assert.False(decoded.Entities[1].Depleted);
    }

    [Fact]
    public void WorldSnapshotRecipientStepSeqRoundTripsEmptyKeepAlive()
    {
        // S76: an empty/keep-alive snapshot (no entity payload, isComplete=false) must still carry the
        // recipient step seq on its header — this is exactly the idle-player case the field must survive.
        var original = new WorldSnapshotMessage(
            serverTick: 9,
            snapshotSequence: 3,
            totalEntities: 4,
            isComplete: false,
            entities: Array.Empty<EntityStateSnapshot>())
        {
            RecipientStepSeq = 4242,
        };

        var decoded = Assert.IsType<WorldSnapshotMessage>(ProtocolCodec.Decode(ProtocolCodec.Encode(original)));

        Assert.Empty(decoded.Entities);
        Assert.False(decoded.IsComplete);
        Assert.Equal(4, decoded.TotalEntities);
        Assert.Equal(4242u, decoded.RecipientStepSeq);
    }

    [Fact]
    public void WorldSnapshotRecipientStepSeqRoundTripsChunked()
    {
        // S76: the seq rides a (non-first) chunk header too — every chunk carries it identically.
        var original = new WorldSnapshotMessage(
            5,
            2,
            10,
            false,
            1,
            3,
            new[] { new EntityStateSnapshot(7, WorldVector.FromTile(1, 2), Direction8.W) },
            RecipientStepSeq: 99);

        var decoded = Assert.IsType<WorldSnapshotMessage>(ProtocolCodec.Decode(ProtocolCodec.Encode(original)));

        Assert.Equal(1, decoded.ChunkIndex);
        Assert.Equal(3, decoded.ChunkCount);
        Assert.Equal(99u, decoded.RecipientStepSeq);
    }

    [Fact]
    public void WorldSnapshotRecipientStepSeqDefaultsToZero()
    {
        // The convenience constructors leave it 0 (no recipient scoping) and that survives the round-trip.
        var original = new WorldSnapshotMessage(
            7,
            [new EntityStateSnapshot(11, WorldVector.FromTile(3, 4), Direction8.S)]);

        var decoded = Assert.IsType<WorldSnapshotMessage>(ProtocolCodec.Decode(ProtocolCodec.Encode(original)));

        Assert.Equal(0u, decoded.RecipientStepSeq);
    }

    // CONTINUOUS MIGRATION (v36): the reshaped per-input MoveIntent round-trips — InputSeq + raw DirX/DirY + DtSeconds.
    [Fact]
    public void MoveIntentRoundTrips()
    {
        var original = new MoveIntentMessage(123, 0.7071f, -0.7071f, 0.0166f);

        var decoded = Assert.IsType<MoveIntentMessage>(ProtocolCodec.Decode(ProtocolCodec.Encode(original)));

        Assert.Equal(123u, decoded.InputSeq);
        Assert.Equal(0.7071f, decoded.DirX);
        Assert.Equal(-0.7071f, decoded.DirY);
        Assert.Equal(0.0166f, decoded.DtSeconds);
    }

    // CONTINUOUS MIGRATION (v36): a (0,0) direction (STOP) round-trips intact.
    [Fact]
    public void MoveIntentStopRoundTrips()
    {
        var original = new MoveIntentMessage(7, 0f, 0f, 0.05f);

        var decoded = Assert.IsType<MoveIntentMessage>(ProtocolCodec.Decode(ProtocolCodec.Encode(original)));

        Assert.Equal(7u, decoded.InputSeq);
        Assert.Equal(0f, decoded.DirX);
        Assert.Equal(0f, decoded.DirY);
        Assert.Equal(0.05f, decoded.DtSeconds);
    }

    [Fact]
    public void SnapshotAckRoundTrips()
    {
        var original = new SnapshotAckMessage(77);

        var decoded = Assert.IsType<SnapshotAckMessage>(ProtocolCodec.Decode(ProtocolCodec.Encode(original)));

        Assert.Equal(77u, decoded.LastSnapshotSequence);
    }

    [Fact]
    public void ServerHelloRoundTripsStepCooldownAndInterestRadius()
    {
        // S98: ServerHello no longer carries turnDelayMs (turn-then-move removed); protocol bumped to v20.
        var original = new ServerHelloMessage("server", ProtocolCodec.Version, 20, 140, 40.5f);

        var decoded = Assert.IsType<ServerHelloMessage>(ProtocolCodec.Decode(ProtocolCodec.Encode(original)));

        Assert.Equal("server", decoded.ServerName);
        Assert.Equal(ProtocolCodec.Version, decoded.ProtocolVersion);
        Assert.Equal(20, decoded.TickRate);
        Assert.Equal(140, decoded.StepCooldownMs);
        Assert.Equal(40.5f, decoded.InterestRadiusTiles);
    }

    [Fact]
    public void ProtocolVersionIsThirtySix()
    {
        // CONTINUOUS MIGRATION (v35 -> v36): the atomic continuous wire break — fixed-point continuous snapshot
        // positions + the LastInputSeq header field + the reshaped per-input MoveIntent (dead tile-step machinery
        // deleted). Mutually undecodable with v35; server + every client ship together. Pin it so a change is caught.
        Assert.Equal(36, ProtocolCodec.Version);
    }

    // LOOT P4c: the corpse loot-window verb round-trips (corpse net id + kind + the template key for TakeItem).
    [Fact]
    public void LootActionRoundTrips()
    {
        var take = new LootActionMessage(4242u, LootActionKind.TakeItem, "slime_gel");
        var decodedTake = Assert.IsType<LootActionMessage>(ProtocolCodec.Decode(ProtocolCodec.Encode(take)));
        Assert.Equal(take, decodedTake);

        var all = new LootActionMessage(7u, LootActionKind.LootAll, string.Empty);
        Assert.Equal(all, ProtocolCodec.Decode(ProtocolCodec.Encode(all)));

        var close = new LootActionMessage(7u, LootActionKind.Close, string.Empty);
        Assert.Equal(close, ProtocolCodec.Decode(ProtocolCodec.Encode(close)));
    }

    // LOOT P4c: an OPEN corpse's contents round-trip — the corpse net id, the Open flag, and each item's template key,
    // quantity, and rarity tier (the wire data the rarity-coloured loot window renders).
    [Fact]
    public void CorpseContentsRoundTrips()
    {
        var original = new CorpseContentsMessage(987u, true, new[]
        {
            new CorpseItem("slime_gel", 3, Rarity.Common),
            new CorpseItem("arcane_dust", 1, Rarity.Rare),
            new CorpseItem("slime_core", 2, Rarity.Legendary),
        });

        var decoded = Assert.IsType<CorpseContentsMessage>(ProtocolCodec.Decode(ProtocolCodec.Encode(original)));

        Assert.Equal(987u, decoded.CorpseNetworkId);
        Assert.True(decoded.Open);
        Assert.Equal(3, decoded.Items.Count);
        Assert.Equal(new CorpseItem("slime_gel", 3, Rarity.Common), decoded.Items[0]);
        Assert.Equal(new CorpseItem("arcane_dust", 1, Rarity.Rare), decoded.Items[1]);
        Assert.Equal(new CorpseItem("slime_core", 2, Rarity.Legendary), decoded.Items[2]);
    }

    // LOOT P4c: a CLOSE (Open=false, empty items) round-trips so the client reliably hides the window.
    [Fact]
    public void CorpseContentsCloseRoundTrips()
    {
        var original = new CorpseContentsMessage(987u, false, System.Array.Empty<CorpseItem>());

        var decoded = Assert.IsType<CorpseContentsMessage>(ProtocolCodec.Decode(ProtocolCodec.Encode(original)));

        Assert.Equal(987u, decoded.CorpseNetworkId);
        Assert.False(decoded.Open);
        Assert.Empty(decoded.Items);
    }

    [Fact]
    public void DamageEventRoundTrips()
    {
        // COMBAT-QOL: the cosmetic damage event round-trips the victim's NetworkId, the damage amount, and the new HP.
        var original = new DamageEventMessage(4242u, 20, 80);

        var decoded = Assert.IsType<DamageEventMessage>(ProtocolCodec.Decode(ProtocolCodec.Encode(original)));

        Assert.Equal(4242u, decoded.NetworkId);
        Assert.Equal(20, decoded.Amount);
        Assert.Equal((ushort)80, decoded.Health);
    }

    [Fact]
    public void AttackMessageRoundTrips()
    {
        // FREEAIM: the attack request round-trips its own sequence + the attack kind + the quantized aim angle.
        // SWING-COMMIT-FIX: it also round-trips the authored tick (the server roots the swing at this tick).
        var original = new AttackMessage(4242, AttackKind.MeleeCone, 12345, 987654);

        var decoded = Assert.IsType<AttackMessage>(ProtocolCodec.Decode(ProtocolCodec.Encode(original)));

        Assert.Equal(4242u, decoded.Sequence);
        Assert.Equal(AttackKind.MeleeCone, decoded.Kind);
        Assert.Equal((ushort)12345, decoded.AimAngle);
        Assert.Equal(987654u, decoded.AuthoredTick);
    }

    [Theory]
    // FREEAIM: the aim quantization is lossy (2π/65536 ≈ 0.0055°), so a decoded angle must land within one
    // quantization step (~0.0001 rad) of the original. Cover the cardinal bearings + the 0/2π seam.
    [InlineData(0.0)]
    [InlineData(1.5707963)]   // +π/2 (south)
    [InlineData(3.1415926)]   // π (west)
    [InlineData(4.7123889)]   // 3π/2 (north)
    [InlineData(6.2831)]      // ~2π, just below the seam
    [InlineData(-0.7853981)]  // negative input normalizes into [0,2π)
    public void AimAngleQuantizationRoundTripsWithinTolerance(double radians)
    {
        var quantized = AimAngle.Quantize(radians);
        var decoded = AimAngle.ToRadians(quantized);

        // Compare on the circle: the smallest signed difference reduced to (-π, π], so the seam wrap is handled.
        var twoPi = 2.0 * System.Math.PI;
        var normalizedInput = ((radians % twoPi) + twoPi) % twoPi;
        var delta = decoded - normalizedInput;
        if (delta > System.Math.PI) delta -= twoPi;
        if (delta <= -System.Math.PI) delta += twoPi;

        // One quantization step is 2π/65536 ≈ 9.6e-5 rad; allow a touch over a full step for the rounding boundary.
        Assert.True(System.Math.Abs(delta) <= twoPi / 65536.0 + 1e-6, $"delta {delta} too large for {radians}");
    }

    [Fact]
    public void AttackMessageRejectsOutOfRangeKind()
    {
        // FREEAIM: a malformed/hostile packet with an unknown attack kind byte is rejected on decode, so the server
        // handler never sees an out-of-range kind. Hand-encode a valid header + seq + a bogus kind byte + aim.
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(ProtocolCodec.Magic);
        writer.Write(ProtocolCodec.Version);
        writer.Write((ushort)MessageType.Attack);
        writer.Write(7u);            // sequence
        writer.Write((byte)200);     // out-of-range AttackKind (rejected before the aim is read)
        writer.Write((ushort)0);     // aim angle
        writer.Flush();

        Assert.Throws<ProtocolException>(() => ProtocolCodec.Decode(stream.ToArray()));
    }

    [Fact]
    public void PlayerStatsRoundTrips()
    {
        var original = new PlayerStatsMessage(new CharacterStats(73, 100, 41, 120, 5, 80));

        var decoded = Assert.IsType<PlayerStatsMessage>(ProtocolCodec.Decode(ProtocolCodec.Encode(original)));

        Assert.Equal(new CharacterStats(73, 100, 41, 120, 5, 80), decoded.Stats);
    }

    [Fact]
    public void CombatTuningRoundTrips()
    {
        // COMBAT-TUNING (v31): the five combat feel-knobs replicate intact, including the fractional geometry
        // (half-angle deg, radius tiles) the panel can nudge.
        var original = new CombatTuningMessage(new CombatTuningSnapshot(
            AttackCooldownMs: 750,
            RootMs: 180,
            HalfAngleDegrees: 37.5,
            RadiusTiles: 2.25,
            Damage: 33));

        var decoded = Assert.IsType<CombatTuningMessage>(ProtocolCodec.Decode(ProtocolCodec.Encode(original)));

        Assert.Equal(750, decoded.Tuning.AttackCooldownMs);
        Assert.Equal(180, decoded.Tuning.RootMs);
        Assert.Equal(37.5, decoded.Tuning.HalfAngleDegrees);
        Assert.Equal(2.25, decoded.Tuning.RadiusTiles);
        Assert.Equal(33, decoded.Tuning.Damage);
    }

    [Fact]
    public void AdminSetStatRoundTrips()
    {
        var original = new AdminSetStatMessage((byte)StatKind.Stamina, -17);

        var decoded = Assert.IsType<AdminSetStatMessage>(ProtocolCodec.Decode(ProtocolCodec.Encode(original)));

        Assert.Equal((byte)StatKind.Stamina, decoded.Stat);
        Assert.Equal(-17, decoded.Value);
    }

    // CONTINUOUS MIGRATION (v36): the tile-step commit/mode message types (StepCommitRequest/MovementMode/MoveInput/
    // StepCommitBatch) are DELETED — their round-trip tests are removed with them.

    [Fact]
    public void EntitySpawnRoundTrips()
    {
        var characterId = Guid.NewGuid();
        var original = new EntitySpawnMessage(
            99,
            characterId,
            EntityKind.Player,
            "PlayerOne",
            new TileCoord(12, 25),
            Direction8.W,
            StepCooldownMs: 70);

        var decoded = Assert.IsType<EntitySpawnMessage>(ProtocolCodec.Decode(ProtocolCodec.Encode(original)));

        Assert.Equal(99u, decoded.NetworkId);
        Assert.Equal(characterId, decoded.CharacterId);
        Assert.Equal(EntityKind.Player, decoded.Kind);
        Assert.Equal("PlayerOne", decoded.DisplayName);
        Assert.Equal(new TileCoord(12, 25), decoded.Tile);
        Assert.Equal(Direction8.W, decoded.Facing);
        Assert.Equal((ushort)70, decoded.StepCooldownMs);
    }

    [Fact]
    public void MovementSpeedChangedRoundTrips()
    {
        var original = new MovementSpeedChangedMessage(99, 70);

        var decoded = Assert.IsType<MovementSpeedChangedMessage>(ProtocolCodec.Decode(ProtocolCodec.Encode(original)));

        Assert.Equal(99u, decoded.NetworkId);
        Assert.Equal((ushort)70, decoded.StepCooldownMs);
    }

    [Fact]
    public void AdminSetTuningRoundTrips()
    {
        var original = new AdminSetTuningMessage("move.stepCooldownMs", 123.5d);

        var decoded = Assert.IsType<AdminSetTuningMessage>(ProtocolCodec.Decode(ProtocolCodec.Encode(original)));

        Assert.Equal("move.stepCooldownMs", decoded.Key);
        Assert.Equal(123.5d, decoded.Value);
    }

    [Fact]
    public void EntityDespawnRoundTrips()
    {
        var original = new EntityDespawnMessage(123, 99);

        var decoded = Assert.IsType<EntityDespawnMessage>(ProtocolCodec.Decode(ProtocolCodec.Encode(original)));

        Assert.Equal(123u, decoded.ServerTick);
        Assert.Equal(99u, decoded.NetworkId);
    }

    [Fact]
    public void DirectServerEncodersMatchProtocolDecoder()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);

        ProtocolCodec.EncodeWorldSnapshot(
            writer,
            42,
            77,
            555,
            909, // CONTINUOUS MIGRATION (v36): lastInputSeq header arg
            120,
            false,
            2,
            6,
            [new EntityStateSnapshot(99, WorldVector.FromTile(12, -25), Direction8.NE)]);
        writer.Flush();
        var snapshot = Assert.IsType<WorldSnapshotMessage>(ProtocolCodec.Decode(stream.ToArray()));
        Assert.Equal(42u, snapshot.ServerTick);
        Assert.Equal(77u, snapshot.SnapshotSequence);
        Assert.Equal(555u, snapshot.RecipientStepSeq);
        Assert.Equal(909u, snapshot.LastInputSeq);
        Assert.Equal(120, snapshot.TotalEntities);
        Assert.False(snapshot.IsComplete);
        Assert.Equal(2, snapshot.ChunkIndex);
        Assert.Equal(6, snapshot.ChunkCount);

        stream.Position = 0;
        stream.SetLength(0);
        ProtocolCodec.EncodeEntityDespawn(writer, 123, 99);
        writer.Flush();
        var despawn = Assert.IsType<EntityDespawnMessage>(ProtocolCodec.Decode(stream.ToArray()));
        Assert.Equal(123u, despawn.ServerTick);
        Assert.Equal(99u, despawn.NetworkId);

        var characterId = Guid.NewGuid();
        stream.Position = 0;
        stream.SetLength(0);
        ProtocolCodec.EncodeEntitySpawn(
            writer,
            7,
            characterId,
            EntityKind.Player,
            "Direct",
            new TileCoord(3, 4),
            Direction8.SW,
            stepCooldownMs: 140);
        writer.Flush();
        var spawn = Assert.IsType<EntitySpawnMessage>(ProtocolCodec.Decode(stream.ToArray()));
        Assert.Equal(7u, spawn.NetworkId);
        Assert.Equal(characterId, spawn.CharacterId);
        Assert.Equal("Direct", spawn.DisplayName);
        Assert.Equal(new TileCoord(3, 4), spawn.Tile);
        Assert.Equal(Direction8.SW, spawn.Facing);
        Assert.Equal((ushort)140, spawn.StepCooldownMs);
    }

    [Fact]
    public void ZoneInfoRoundTrips()
    {
        var original = new ZoneInfoMessage(
            "sandbox",
            16,
            12,
            Seed: 1234,
            GenVersion: 1,
            ContentHash: 0xDEADBEEFCAFEF00DUL);

        var decoded = Assert.IsType<ZoneInfoMessage>(ProtocolCodec.Decode(ProtocolCodec.Encode(original)));

        Assert.Equal("sandbox", decoded.ZoneId);
        Assert.Equal(16, decoded.Width);
        Assert.Equal(12, decoded.Height);
        Assert.Equal(1234, decoded.Seed);
        Assert.Equal(1, decoded.GenVersion);
        Assert.Equal(0xDEADBEEFCAFEF00DUL, decoded.ContentHash);
    }

    [Fact]
    public void LoginResultRoundTripsRole()
    {
        var characterId = Guid.NewGuid();
        var original = new LoginResultMessage(
            true,
            characterId,
            "Admin",
            ClientRole.Admin,
            new TileCoord(3, 4),
            "");

        var decoded = Assert.IsType<LoginResultMessage>(ProtocolCodec.Decode(ProtocolCodec.Encode(original)));

        Assert.Equal(characterId, decoded.CharacterId);
        Assert.Equal("Admin", decoded.DisplayName);
        Assert.Equal(ClientRole.Admin, decoded.Role);
        Assert.Equal(new TileCoord(3, 4), decoded.Tile);
    }

    [Fact]
    public void InvalidMagicThrows()
    {
        var packet = ProtocolCodec.Encode(new ClientHelloMessage("client"));
        packet[0] = 0;

        Assert.Throws<ProtocolException>(() => ProtocolCodec.Decode(packet));
    }

    [Fact]
    public void EncodeCanReuseWriterBuffer()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);

        ProtocolCodec.Encode(new ChatSendMessage("first payload"), writer);
        writer.Flush();

        stream.Position = 0;
        stream.SetLength(0);
        ProtocolCodec.Encode(new MoveIntentMessage(42, -0.7071f, -0.7071f, 0.05f), writer);
        writer.Flush();

        var packet = stream.ToArray();
        var decoded = Assert.IsType<MoveIntentMessage>(ProtocolCodec.Decode(packet));
        Assert.Equal(42u, decoded.InputSeq);
        Assert.Equal(-0.7071f, decoded.DirX);
        Assert.Equal(-0.7071f, decoded.DirY);
        Assert.Equal(0.05f, decoded.DtSeconds);
    }
}
