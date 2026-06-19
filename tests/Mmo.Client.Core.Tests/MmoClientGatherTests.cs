using Mmo.Shared.Domain;
using Mmo.Shared.Protocol;
using Xunit;

namespace Mmo.Client.Core.Tests;

public sealed class MmoClientGatherTests
{
    [Fact]
    public void SendInteractRequestEmitsReliableMessageWithTarget()
    {
        using var client = CreateClient(out var outbound);

        client.SendInteractRequest(42);

        var request = Assert.Single(outbound.OfType<InteractRequestMessage>());
        Assert.Equal(42u, request.TargetNetworkId);
    }

    [Fact]
    public void InventoryUpdateUpdatesClientInventory()
    {
        using var client = CreateClient(out _);

        client.HandleMessageForTests(new InventoryUpdateMessage([new ItemStack("wood", 3)]));
        Assert.Equal(3, client.Inventory.QuantityOf("wood"));

        client.HandleMessageForTests(new InventoryUpdateMessage([new ItemStack("wood", 4)]));
        Assert.Equal(4, client.Inventory.QuantityOf("wood"));
    }

    [Fact]
    public void InteractResultSurfacesWithMonotonicSequence()
    {
        using var client = CreateClient(out _);

        Assert.Null(client.LastInteractResult);

        client.HandleMessageForTests(new InteractResultMessage(true, ""));
        var first = client.LastInteractResult;
        Assert.NotNull(first);
        Assert.True(first!.Value.Success);

        client.HandleMessageForTests(new InteractResultMessage(false, "too_far"));
        var second = client.LastInteractResult;
        Assert.NotNull(second);
        Assert.False(second!.Value.Success);
        Assert.Equal("too_far", second.Value.Reason);
        // Two distinct failures must be distinguishable even with the same reason: the sequence advances.
        Assert.True(second.Value.Sequence > first.Value.Sequence);

        client.HandleMessageForTests(new InteractResultMessage(false, "too_far"));
        Assert.True(client.LastInteractResult!.Value.Sequence > second.Value.Sequence);
    }

    [Fact]
    public void SnapshotDepletedBitThreadsThroughToRenderState()
    {
        using var client = CreateClient(out _);

        client.HandleMessageForTests(Snapshot(1, isComplete: true,
            new EntityStateSnapshot(7, new TileCoord(3, 3), Direction8.S, Depleted: true)));

        var render = Assert.Single(client.GetRenderStates(TimeSpan.Zero));
        Assert.True(render.Depleted);

        // Respawn (Depleted=false) flips it back.
        client.HandleMessageForTests(Snapshot(2, isComplete: true,
            new EntityStateSnapshot(7, new TileCoord(3, 3), Direction8.S, Depleted: false)));

        Assert.False(Assert.Single(client.GetRenderStates(TimeSpan.Zero)).Depleted);
    }

    private static MmoClient CreateClient(out List<IProtocolMessage> outbound)
    {
        outbound = [];
        var captured = outbound;
        var client = new MmoClient(
            new ClientConnectionOptions("127.0.0.1", 1, "test", "account", "display"),
            new ClientMovementTrace(false, null));
        client.OutboundSinkForTests = (message, _) => captured.Add(message);
        return client;
    }

    private static WorldSnapshotMessage Snapshot(uint sequence, bool isComplete, params EntityStateSnapshot[] entities)
    {
        return new WorldSnapshotMessage(10, sequence, entities.Length, isComplete, 0, 1, entities);
    }
}
