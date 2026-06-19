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
    public void MoveStepRoundTrips()
    {
        var original = new MoveStepMessage(123, Direction8.SW);

        var decoded = Assert.IsType<MoveStepMessage>(ProtocolCodec.Decode(ProtocolCodec.Encode(original)));

        Assert.Equal(123u, decoded.Sequence);
        Assert.Equal(Direction8.SW, decoded.Direction);
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
            [
                new TileCoord(0, 0),
                new TileCoord(4, 7),
                new TileCoord(15, 11)
            ]);

        var decoded = Assert.IsType<ZoneInfoMessage>(ProtocolCodec.Decode(ProtocolCodec.Encode(original)));

        Assert.Equal("sandbox", decoded.ZoneId);
        Assert.Equal(16, decoded.Width);
        Assert.Equal(12, decoded.Height);
        Assert.Equal(original.BlockedTiles, decoded.BlockedTiles);
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
        ProtocolCodec.Encode(new MoveStepMessage(42, Direction8.NW), writer);
        writer.Flush();

        var packet = stream.ToArray();
        var decoded = Assert.IsType<MoveStepMessage>(ProtocolCodec.Decode(packet));
        Assert.Equal(42u, decoded.Sequence);
        Assert.Equal(Direction8.NW, decoded.Direction);
    }
}
