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

    // LOOT P4c: an OPEN CorpseContents fills the client's loot mirror with rarity-tagged rows + the corpse id; a CLOSE
    // (Open=false) clears it. CorpseLootVersion bumps on each so the HUD rebuilds only on change.
    [Fact]
    public void CorpseContentsOpensAndClosesTheLootMirror()
    {
        using var client = CreateClient(out _);
        Assert.Null(client.CorpseLoot);
        var v0 = client.CorpseLootVersion;

        client.HandleMessageForTests(new CorpseContentsMessage(55, true, new[]
        {
            new CorpseItem("slime_gel", 3, Rarity.Common),
            new CorpseItem("slime_core", 1, Rarity.Legendary),
        }));

        Assert.NotNull(client.CorpseLoot);
        Assert.Equal(55u, client.CorpseLoot!.CorpseNetworkId);
        Assert.True(client.CorpseLootVersion > v0);

        var rows = client.CorpseLoot.ToRows(ItemRegistry.Default);
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.TemplateKey == "slime_gel" && r.Quantity == 3 && r.Rarity == Rarity.Common && r.DisplayName == "Slime Gel");
        Assert.Contains(rows, r => r.TemplateKey == "slime_core" && r.Rarity == Rarity.Legendary);

        var vOpen = client.CorpseLootVersion;
        client.HandleMessageForTests(new CorpseContentsMessage(55, false, System.Array.Empty<CorpseItem>()));
        Assert.Null(client.CorpseLoot);
        Assert.True(client.CorpseLootVersion > vOpen);
    }

    // LOOT P4c: the loot-window send verbs emit the right reliable LootAction (kind + corpse id + key); Close also
    // clears the local mirror immediately so the panel hides without waiting for the server round-trip.
    [Fact]
    public void LootActionSendsEmitTheRightVerbs()
    {
        using var client = CreateClient(out var outbound);

        client.SendLootItem(99, "arcane_dust");
        var take = Assert.Single(outbound.OfType<LootActionMessage>());
        Assert.Equal(99u, take.CorpseNetworkId);
        Assert.Equal(LootActionKind.TakeItem, take.Kind);
        Assert.Equal("arcane_dust", take.TemplateKey);

        outbound.Clear();
        client.SendLootAll(99);
        var all = Assert.Single(outbound.OfType<LootActionMessage>());
        Assert.Equal(LootActionKind.LootAll, all.Kind);

        // Open a window, then Close: the close verb is sent AND the mirror clears immediately.
        client.HandleMessageForTests(new CorpseContentsMessage(99, true, new[] { new CorpseItem("slime_gel", 1, Rarity.Common) }));
        Assert.NotNull(client.CorpseLoot);
        outbound.Clear();
        client.SendCloseLoot(99);
        var close = Assert.Single(outbound.OfType<LootActionMessage>());
        Assert.Equal(LootActionKind.Close, close.Kind);
        Assert.Null(client.CorpseLoot);
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
