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
            });

        var decoded = Assert.IsType<WorldSnapshotMessage>(ProtocolCodec.Decode(ProtocolCodec.Encode(original)));

        Assert.Equal(original.ServerTick, decoded.ServerTick);
        Assert.Equal(77u, decoded.SnapshotSequence);
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
    public void WorldSnapshotRoundTripsStepDeltaRow()
    {
        var original = new WorldSnapshotMessage(
            5,
            9,
            3,
            isComplete: false,
            new[]
            {
                // Step-delta position only (facing/depleted unchanged → omitted).
                new EntityStateSnapshot(11, TileCoord.Zero, Direction8.N, false, EntityStateChange.PositionStep, Direction8.E),
            });

        var decoded = Assert.IsType<WorldSnapshotMessage>(ProtocolCodec.Decode(ProtocolCodec.Encode(original)));

        var row = Assert.Single(decoded.Entities);
        Assert.Equal(11u, row.NetworkId);
        Assert.True(row.HasStepPosition);
        Assert.False(row.HasAbsolutePosition);
        Assert.False(row.HasFacing);
        Assert.False(row.HasDepleted);
        Assert.Equal(Direction8.E, row.Step);
    }

    [Fact]
    public void WorldSnapshotRoundTripsMixedChangedFields()
    {
        var original = new WorldSnapshotMessage(
            5,
            9,
            4,
            isComplete: false,
            new[]
            {
                // Step move + facing change, depleted unchanged.
                new EntityStateSnapshot(1, TileCoord.Zero, Direction8.SW, false, EntityStateChange.PositionStep | EntityStateChange.Facing, Direction8.SW),
                // Absolute teleport + depleted change, facing unchanged.
                new EntityStateSnapshot(2, new TileCoord(-30, 40), Direction8.N, true, EntityStateChange.PositionAbsolute | EntityStateChange.Depleted, Direction8.N),
                // Facing-only change (resource node turning in place would be position-omitted).
                new EntityStateSnapshot(3, TileCoord.Zero, Direction8.W, false, EntityStateChange.Facing, Direction8.N),
                // Depleted-only change (harvested node, position unchanged).
                new EntityStateSnapshot(4, TileCoord.Zero, Direction8.N, true, EntityStateChange.Depleted, Direction8.N),
            });

        var decoded = Assert.IsType<WorldSnapshotMessage>(ProtocolCodec.Decode(ProtocolCodec.Encode(original)));

        Assert.Equal(4, decoded.Entities.Count);

        var step = decoded.Entities[0];
        Assert.True(step.HasStepPosition);
        Assert.True(step.HasFacing);
        Assert.Equal(Direction8.SW, step.Step);
        Assert.Equal(Direction8.SW, step.Facing);

        var teleport = decoded.Entities[1];
        Assert.True(teleport.HasAbsolutePosition);
        Assert.True(teleport.HasDepleted);
        Assert.False(teleport.HasFacing);
        Assert.Equal(new TileCoord(-30, 40), teleport.Tile);
        Assert.True(teleport.Depleted);

        var facingOnly = decoded.Entities[2];
        Assert.False(facingOnly.HasAbsolutePosition);
        Assert.False(facingOnly.HasStepPosition);
        Assert.True(facingOnly.HasFacing);
        Assert.Equal(Direction8.W, facingOnly.Facing);

        var depletedOnly = decoded.Entities[3];
        Assert.False(depletedOnly.HasAbsolutePosition);
        Assert.False(depletedOnly.HasStepPosition);
        Assert.True(depletedOnly.HasDepleted);
        Assert.True(depletedOnly.Depleted);
    }

    [Fact]
    public void WorldSnapshotAbsoluteRowRoundTripsAllFields()
    {
        var original = new WorldSnapshotMessage(
            1,
            1,
            1,
            isComplete: true,
            new[] { EntityStateSnapshot.Absolute(7, new TileCoord(12, -25), Direction8.NE, depleted: true) });

        var decoded = Assert.IsType<WorldSnapshotMessage>(ProtocolCodec.Decode(ProtocolCodec.Encode(original)));

        var row = Assert.Single(decoded.Entities);
        Assert.True(row.HasAbsolutePosition);
        Assert.True(row.HasFacing);
        Assert.True(row.HasDepleted);
        Assert.Equal(new TileCoord(12, -25), row.Tile);
        Assert.Equal(Direction8.NE, row.Facing);
        Assert.True(row.Depleted);
    }

    [Fact]
    public void ProtocolVersionIsSixteen()
    {
        Assert.Equal(16, ProtocolCodec.Version);
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
        var original = new ServerHelloMessage("server", ProtocolCodec.Version, 20, 140, 40.5f);

        var decoded = Assert.IsType<ServerHelloMessage>(ProtocolCodec.Decode(ProtocolCodec.Encode(original)));

        Assert.Equal("server", decoded.ServerName);
        Assert.Equal(ProtocolCodec.Version, decoded.ProtocolVersion);
        Assert.Equal(20, decoded.TickRate);
        Assert.Equal(140, decoded.StepCooldownMs);
        Assert.Equal(40.5f, decoded.InterestRadiusTiles);
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
            Direction8.W);

        var decoded = Assert.IsType<EntitySpawnMessage>(ProtocolCodec.Decode(ProtocolCodec.Encode(original)));

        Assert.Equal(99u, decoded.NetworkId);
        Assert.Equal(characterId, decoded.CharacterId);
        Assert.Equal(EntityKind.Player, decoded.Kind);
        Assert.Equal("PlayerOne", decoded.DisplayName);
        Assert.Equal(new TileCoord(12, 25), decoded.Tile);
        Assert.Equal(Direction8.W, decoded.Facing);
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
            120,
            false,
            2,
            6,
            [new EntityStateSnapshot(99, new TileCoord(12, -25), Direction8.NE)]);
        writer.Flush();
        var snapshot = Assert.IsType<WorldSnapshotMessage>(ProtocolCodec.Decode(stream.ToArray()));
        Assert.Equal(42u, snapshot.ServerTick);
        Assert.Equal(77u, snapshot.SnapshotSequence);
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
            Direction8.SW);
        writer.Flush();
        var spawn = Assert.IsType<EntitySpawnMessage>(ProtocolCodec.Decode(stream.ToArray()));
        Assert.Equal(7u, spawn.NetworkId);
        Assert.Equal(characterId, spawn.CharacterId);
        Assert.Equal("Direct", spawn.DisplayName);
        Assert.Equal(new TileCoord(3, 4), spawn.Tile);
        Assert.Equal(Direction8.SW, spawn.Facing);
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
