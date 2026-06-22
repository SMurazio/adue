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
                new EntityStateSnapshot(99, new TileCoord(12, -25), Direction8.NE)
            },
            RecipientStepSeq: 555);

        var decoded = Assert.IsType<WorldSnapshotMessage>(ProtocolCodec.Decode(ProtocolCodec.Encode(original)));

        Assert.Equal(original.ServerTick, decoded.ServerTick);
        Assert.Equal(77u, decoded.SnapshotSequence);
        Assert.Equal(555u, decoded.RecipientStepSeq);
        Assert.Equal(120, decoded.TotalEntities);
        Assert.False(decoded.IsComplete);
        Assert.Equal(2, decoded.ChunkIndex);
        Assert.Equal(6, decoded.ChunkCount);
        var entity = Assert.Single(decoded.Entities);
        Assert.Equal(99u, entity.NetworkId);
        Assert.Equal(new TileCoord(12, -25), entity.Tile);
        Assert.Equal(Direction8.NE, entity.Facing);
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
                new EntityStateSnapshot(31, new TileCoord(8, 9), Direction8.S, Depleted: false, Health: 70, MaxHealth: 100),
                new EntityStateSnapshot(32, new TileCoord(2, 3), Direction8.N, Depleted: false, Health: 0, MaxHealth: 0),
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
                new EntityStateSnapshot(11, new TileCoord(3, 4), Direction8.S, Depleted: true),
                new EntityStateSnapshot(12, new TileCoord(5, 6), Direction8.N, Depleted: false),
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
            new[] { new EntityStateSnapshot(7, new TileCoord(1, 2), Direction8.W) },
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
            [new EntityStateSnapshot(11, new TileCoord(3, 4), Direction8.S)]);

        var decoded = Assert.IsType<WorldSnapshotMessage>(ProtocolCodec.Decode(ProtocolCodec.Encode(original)));

        Assert.Equal(0u, decoded.RecipientStepSeq);
    }

    [Fact]
    public void MoveIntentRoundTrips()
    {
        var original = new MoveIntentMessage(123, true, Direction8.SW);

        var decoded = Assert.IsType<MoveIntentMessage>(ProtocolCodec.Decode(ProtocolCodec.Encode(original)));

        Assert.Equal(123u, decoded.Sequence);
        Assert.True(decoded.Moving);
        Assert.Equal(Direction8.SW, decoded.Direction);
    }

    [Fact]
    public void MoveIntentStoppedRoundTrips()
    {
        var original = new MoveIntentMessage(7, false, Direction8.N);

        var decoded = Assert.IsType<MoveIntentMessage>(ProtocolCodec.Decode(ProtocolCodec.Encode(original)));

        Assert.Equal(7u, decoded.Sequence);
        Assert.False(decoded.Moving);
        Assert.Equal(Direction8.N, decoded.Direction);
    }

    // NET1 Stage 1: the redundant MoveInput packet round-trips its full head state plus the window of prior
    // inputs (deltas off HeadSeq). Window entries carry their own Moving/Direction.
    [Fact]
    public void MoveInputRoundTripsHeadAndWindow()
    {
        var original = new MoveInputMessage(
            HeadSeq: 100,
            Moving: true,
            Direction: Direction8.E,
            Window:
            [
                new MoveInputWindowEntry(SeqDelta: 1, Moving: true, Direction: Direction8.NE),
                new MoveInputWindowEntry(SeqDelta: 2, Moving: false, Direction: Direction8.S),
                new MoveInputWindowEntry(SeqDelta: 3, Moving: true, Direction: Direction8.W),
            ]);

        var decoded = Assert.IsType<MoveInputMessage>(ProtocolCodec.Decode(ProtocolCodec.Encode(original)));

        Assert.Equal(100u, decoded.HeadSeq);
        Assert.True(decoded.Moving);
        Assert.Equal(Direction8.E, decoded.Direction);
        Assert.Equal(3, decoded.Window.Count);
        Assert.Equal(new MoveInputWindowEntry(1, true, Direction8.NE), decoded.Window[0]);
        Assert.Equal(new MoveInputWindowEntry(2, false, Direction8.S), decoded.Window[1]);
        Assert.Equal(new MoveInputWindowEntry(3, true, Direction8.W), decoded.Window[2]);
    }

    [Fact]
    public void MoveInputRoundTripsEmptyWindow()
    {
        var original = new MoveInputMessage(5, false, Direction8.N, Window: []);

        var decoded = Assert.IsType<MoveInputMessage>(ProtocolCodec.Decode(ProtocolCodec.Encode(original)));

        Assert.Equal(5u, decoded.HeadSeq);
        Assert.False(decoded.Moving);
        Assert.Equal(Direction8.N, decoded.Direction);
        Assert.Empty(decoded.Window);
    }

    // NET2/NET3: the redundant StepCommitBatch round-trips its newest committed step (head seq + AUTHORED tick +
    // direction) plus the window of prior committed steps (seq/tick deltas off the head). Window entries carry their
    // own Direction (no Moving flag — a commit is always a step) and a per-entry tick delta.
    [Fact]
    public void StepCommitBatchRoundTripsHeadAndWindow()
    {
        var original = new StepCommitBatchMessage(
            HeadSeq: 200,
            HeadTick: 1000,
            Direction: Direction8.E,
            Window:
            [
                new StepCommitWindowEntry(SeqDelta: 1, TickDelta: 3, Direction: Direction8.NE),
                new StepCommitWindowEntry(SeqDelta: 2, TickDelta: 6, Direction: Direction8.S),
                new StepCommitWindowEntry(SeqDelta: 5, TickDelta: 15, Direction: Direction8.W),
            ]);

        var decoded = Assert.IsType<StepCommitBatchMessage>(ProtocolCodec.Decode(ProtocolCodec.Encode(original)));

        Assert.Equal(200u, decoded.HeadSeq);
        Assert.Equal(1000u, decoded.HeadTick);
        Assert.Equal(Direction8.E, decoded.Direction);
        Assert.Equal(3, decoded.Window.Count);
        Assert.Equal(new StepCommitWindowEntry(1, 3, Direction8.NE), decoded.Window[0]);
        Assert.Equal(new StepCommitWindowEntry(2, 6, Direction8.S), decoded.Window[1]);
        Assert.Equal(new StepCommitWindowEntry(5, 15, Direction8.W), decoded.Window[2]);
    }

    [Fact]
    public void StepCommitBatchRoundTripsEmptyWindow()
    {
        var original = new StepCommitBatchMessage(7, 42, Direction8.SW, Window: []);

        var decoded = Assert.IsType<StepCommitBatchMessage>(ProtocolCodec.Decode(ProtocolCodec.Encode(original)));

        Assert.Equal(7u, decoded.HeadSeq);
        Assert.Equal(42u, decoded.HeadTick);
        Assert.Equal(Direction8.SW, decoded.Direction);
        Assert.Empty(decoded.Window);
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
    public void ProtocolVersionIsThirty()
    {
        // SWING-COMMIT-FIX protocol bump (v29 -> v30): AttackMessage gains an authored tick (uint) so the server can
        // root the swing at the same logical tick the predictor did (killing the swing-then-move rubberband under
        // latency). A wire layout change is breaking (server + client ship together). Pin the version so an accidental
        // change is caught.
        Assert.Equal(30, ProtocolCodec.Version);
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
    public void AdminSetStatRoundTrips()
    {
        var original = new AdminSetStatMessage((byte)StatKind.Stamina, -17);

        var decoded = Assert.IsType<AdminSetStatMessage>(ProtocolCodec.Decode(ProtocolCodec.Encode(original)));

        Assert.Equal((byte)StatKind.Stamina, decoded.Stat);
        Assert.Equal(-17, decoded.Value);
    }

    [Fact]
    public void StepCommitRequestRoundTrips()
    {
        var original = new StepCommitRequestMessage(4242, Direction8.SW);

        var decoded = Assert.IsType<StepCommitRequestMessage>(ProtocolCodec.Decode(ProtocolCodec.Encode(original)));

        Assert.Equal(4242u, decoded.Sequence);
        Assert.Equal(Direction8.SW, decoded.Direction);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MovementModeRoundTrips(bool clientDriven)
    {
        // UO1: the one-bit client-driven movement signal round-trips both ways.
        var original = new MovementModeMessage(clientDriven);

        var decoded = Assert.IsType<MovementModeMessage>(ProtocolCodec.Decode(ProtocolCodec.Encode(original)));

        Assert.Equal(clientDriven, decoded.ClientDriven);
    }

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
            120,
            false,
            2,
            6,
            [new EntityStateSnapshot(99, new TileCoord(12, -25), Direction8.NE)]);
        writer.Flush();
        var snapshot = Assert.IsType<WorldSnapshotMessage>(ProtocolCodec.Decode(stream.ToArray()));
        Assert.Equal(42u, snapshot.ServerTick);
        Assert.Equal(77u, snapshot.SnapshotSequence);
        Assert.Equal(555u, snapshot.RecipientStepSeq);
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
        ProtocolCodec.Encode(new MoveIntentMessage(42, true, Direction8.NW), writer);
        writer.Flush();

        var packet = stream.ToArray();
        var decoded = Assert.IsType<MoveIntentMessage>(ProtocolCodec.Decode(packet));
        Assert.Equal(42u, decoded.Sequence);
        Assert.True(decoded.Moving);
        Assert.Equal(Direction8.NW, decoded.Direction);
    }
}
