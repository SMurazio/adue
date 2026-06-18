using Mmo.Shared.Domain;
using Mmo.Shared.Protocol;
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
                new EntityStateSnapshot(99, new WorldVector(1.25f, -2.5f))
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
        Assert.InRange(entity.Position.X, 1.2f, 1.3f);
        Assert.Equal(-2.5f, entity.Position.Y);
    }

    [Fact]
    public void SnapshotAckRoundTrips()
    {
        var original = new SnapshotAckMessage(77);

        var decoded = Assert.IsType<SnapshotAckMessage>(ProtocolCodec.Decode(ProtocolCodec.Encode(original)));

        Assert.Equal(77u, decoded.LastSnapshotSequence);
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
            new WorldVector(1.25f, -2.5f));

        var decoded = Assert.IsType<EntitySpawnMessage>(ProtocolCodec.Decode(ProtocolCodec.Encode(original)));

        Assert.Equal(99u, decoded.NetworkId);
        Assert.Equal(characterId, decoded.CharacterId);
        Assert.Equal(EntityKind.Player, decoded.Kind);
        Assert.Equal("PlayerOne", decoded.DisplayName);
        Assert.Equal(1.25f, decoded.Position.X);
        Assert.Equal(-2.5f, decoded.Position.Y);
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
    public void LoginResultRoundTripsRole()
    {
        var characterId = Guid.NewGuid();
        var original = new LoginResultMessage(
            true,
            characterId,
            "Admin",
            ClientRole.Admin,
            new WorldVector(3.5f, 4.5f),
            "");

        var decoded = Assert.IsType<LoginResultMessage>(ProtocolCodec.Decode(ProtocolCodec.Encode(original)));

        Assert.Equal(characterId, decoded.CharacterId);
        Assert.Equal("Admin", decoded.DisplayName);
        Assert.Equal(ClientRole.Admin, decoded.Role);
        Assert.Equal(3.5f, decoded.Position.X);
        Assert.Equal(4.5f, decoded.Position.Y);
    }

    [Fact]
    public void InvalidMagicThrows()
    {
        var packet = ProtocolCodec.Encode(new ClientHelloMessage("client"));
        packet[0] = 0;

        Assert.Throws<ProtocolException>(() => ProtocolCodec.Decode(packet));
    }
}
