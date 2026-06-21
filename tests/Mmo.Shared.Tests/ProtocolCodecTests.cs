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
    public void ProtocolVersionIsTwentyFive()
    {
        // NET3 protocol bump: extending StepCommitBatch with a per-commit authored tick (HeadTick + per-window-entry
        // TickDelta) so the server applies each commit at its authored time is a breaking wire change (server +
        // client ship together). v24 was NET2's StepCommitBatch; v23 was NET1's MoveInput. Pin the version so an
        // accidental change is caught.
        Assert.Equal(25, ProtocolCodec.Version);
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
