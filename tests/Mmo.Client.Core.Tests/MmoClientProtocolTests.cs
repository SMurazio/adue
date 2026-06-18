using LiteNetLib;
using Mmo.Shared.Domain;
using Mmo.Shared.Protocol;
using Xunit;

namespace Mmo.Client.Core.Tests;

public sealed class MmoClientProtocolTests
{
    [Fact]
    public void ChunkedSnapshotAppliesAndAcksOnceAfterReassembly()
    {
        using var client = CreateClient(out var outbound);

        client.HandleMessageForTests(new WorldSnapshotMessage(
            10,
            7,
            2,
            true,
            0,
            2,
            [State(1, 10, 10)]));

        Assert.Empty(outbound);
        Assert.False(client.TryGetEntity(1, out _));

        client.HandleMessageForTests(new WorldSnapshotMessage(
            10,
            7,
            2,
            true,
            1,
            2,
            [State(2, 11, 10)]));

        var ack = Assert.Single(outbound.OfType<SnapshotAckMessage>());
        Assert.Equal(7u, ack.LastSnapshotSequence);
        Assert.True(client.TryGetEntity(1, out _));
        Assert.True(client.TryGetEntity(2, out _));
    }

    [Fact]
    public void InvalidChunkAndStaleSnapshotAreDroppedWithoutAckOrStateChange()
    {
        using var client = CreateClient(out var outbound);

        client.HandleMessageForTests(new WorldSnapshotMessage(
            10,
            1,
            1,
            true,
            2,
            2,
            [State(1, 99, 99)]));

        Assert.Empty(outbound);
        Assert.False(client.TryGetEntity(1, out _));

        client.HandleMessageForTests(Snapshot(2, isComplete: true, State(1, 1, 1)));
        Assert.Single(outbound.OfType<SnapshotAckMessage>());
        Assert.True(client.TryGetEntity(1, out var applied));
        Assert.Equal(new TileCoord(1, 1), applied.Tile);

        client.HandleMessageForTests(Snapshot(1, isComplete: true, State(1, 5, 5)));

        Assert.Single(outbound.OfType<SnapshotAckMessage>());
        Assert.True(client.TryGetEntity(1, out var current));
        Assert.Equal(new TileCoord(1, 1), current.Tile);
    }

    [Fact]
    public void IncompleteSnapshotMergesAndFullSnapshotPrunesMissingEntities()
    {
        using var client = CreateClient(out _);

        client.HandleMessageForTests(Snapshot(1, isComplete: true, State(1, 1, 1), State(2, 2, 2)));
        client.HandleMessageForTests(Snapshot(2, isComplete: false, State(1, 3, 1)));

        Assert.True(client.TryGetEntity(1, out var moved));
        Assert.Equal(new TileCoord(3, 1), moved.Tile);
        Assert.True(client.TryGetEntity(2, out _));

        client.HandleMessageForTests(Snapshot(3, isComplete: true, State(1, 4, 1)));

        Assert.True(client.TryGetEntity(1, out var retained));
        Assert.Equal(new TileCoord(4, 1), retained.Tile);
        Assert.False(client.TryGetEntity(2, out _));
    }

    [Fact]
    public void PlaceholderFromSnapshotIsUpgradedByEntitySpawn()
    {
        using var client = CreateClient(out _);
        var characterId = Guid.NewGuid();

        client.HandleMessageForTests(Snapshot(1, isComplete: true, State(42, 8, 9)));
        Assert.True(client.TryGetEntity(42, out var placeholder));
        Assert.Equal("#42", placeholder.DisplayName);
        Assert.Equal(Guid.Empty, placeholder.CharacterId);

        client.HandleMessageForTests(new EntitySpawnMessage(
            42,
            characterId,
            EntityKind.Player,
            "RealName",
            new TileCoord(8, 9),
            Direction8.S));

        Assert.True(client.TryGetEntity(42, out var upgraded));
        Assert.Equal(characterId, upgraded.CharacterId);
        Assert.Equal("RealName", upgraded.DisplayName);
    }

    [Fact]
    public void PlaceholderAbsentFromIncompleteSnapshotsExpires()
    {
        using var client = CreateClient(out _);

        client.HandleMessageForTests(Snapshot(1, isComplete: false, State(42, 1, 1)));
        Assert.True(client.TryGetEntity(42, out _));

        for (var sequence = 2u; sequence <= 63u; sequence++)
        {
            client.HandleMessageForTests(Snapshot(sequence, isComplete: false, State(99, 2, 2)));
        }

        Assert.False(client.TryGetEntity(42, out _));
        Assert.True(client.TryGetEntity(99, out _));
    }

    [Fact]
    public void ServerHelloRefreshesInterpolatorCadenceForEntitiesCreatedEarly()
    {
        using var client = CreateClient(out _);

        client.HandleMessageForTests(Snapshot(1, isComplete: true, State(7, 0, 0)));
        client.HandleMessageForTests(new ServerHelloMessage("test", ProtocolCodec.Version, 20, 300, 30));
        client.HandleMessageForTests(Snapshot(2, isComplete: false, State(7, 1, 0)));

        var render = Assert.Single(client.GetRenderStates(TimeSpan.FromMilliseconds(200)));
        Assert.Equal(0, render.Position.X);
    }

    [Fact]
    public void EntityDespawnClearsLocalNetworkId()
    {
        using var client = CreateClient(out _);
        var characterId = Guid.NewGuid();

        client.HandleMessageForTests(new LoginResultMessage(true, characterId, "Local", ClientRole.Player, new TileCoord(3, 3), ""));
        client.HandleMessageForTests(new EntitySpawnMessage(9, characterId, EntityKind.Player, "Local", new TileCoord(3, 3), Direction8.S));

        Assert.Equal(9u, client.LocalNetworkId);
        Assert.True(client.TryGetEntity(9, out var local));
        Assert.True(local.IsLocal);

        client.HandleMessageForTests(new EntityDespawnMessage(3, 9));

        Assert.Null(client.LocalNetworkId);
        Assert.False(client.TryGetEntity(9, out _));
    }

    private static MmoClient CreateClient(out List<IProtocolMessage> outbound)
    {
        outbound = [];
        var captured = outbound;
        var client = new MmoClient(new ClientConnectionOptions("127.0.0.1", 1, "test", "account", "display"));
        client.OutboundSinkForTests = (message, _) => captured.Add(message);
        return client;
    }

    private static WorldSnapshotMessage Snapshot(uint sequence, bool isComplete, params EntityStateSnapshot[] entities)
    {
        return new WorldSnapshotMessage(10, sequence, entities.Length, isComplete, 0, 1, entities);
    }

    private static EntityStateSnapshot State(uint networkId, int x, int y)
    {
        return new EntityStateSnapshot(networkId, new TileCoord(x, y), Direction8.S);
    }
}
