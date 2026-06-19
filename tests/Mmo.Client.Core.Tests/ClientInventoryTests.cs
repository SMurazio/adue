using Mmo.Shared.Domain;
using Xunit;

namespace Mmo.Client.Core.Tests;

public sealed class ClientInventoryTests
{
    [Fact]
    public void ApplySetsAuthoritativeTotalsAndBumpsVersion()
    {
        var inventory = new ClientInventory();
        var initialVersion = inventory.Version;

        var changed = inventory.Apply([new ItemStack("wood", 5)]);

        Assert.True(changed);
        Assert.Equal(5, inventory.QuantityOf("wood"));
        Assert.Equal(1, inventory.DistinctItemCount);
        Assert.True(inventory.Version > initialVersion);
    }

    [Fact]
    public void ApplyReplacesQuantityRatherThanAdding()
    {
        var inventory = new ClientInventory();
        inventory.Apply([new ItemStack("wood", 5)]);

        // The server sends the new authoritative total, not a delta.
        inventory.Apply([new ItemStack("wood", 7)]);

        Assert.Equal(7, inventory.QuantityOf("wood"));
    }

    [Fact]
    public void ApplyZeroQuantityRemovesStack()
    {
        var inventory = new ClientInventory();
        inventory.Apply([new ItemStack("stone", 3)]);

        var changed = inventory.Apply([new ItemStack("stone", 0)]);

        Assert.True(changed);
        Assert.Equal(0, inventory.QuantityOf("stone"));
        Assert.Equal(0, inventory.DistinctItemCount);
    }

    [Fact]
    public void ApplyNoOpDeltaDoesNotBumpVersion()
    {
        var inventory = new ClientInventory();
        inventory.Apply([new ItemStack("fiber", 2)]);
        var version = inventory.Version;

        var changed = inventory.Apply([new ItemStack("fiber", 2)]);

        Assert.False(changed);
        Assert.Equal(version, inventory.Version);
    }

    [Fact]
    public void ToOrderedRowsResolvesRegistryDisplayNamesInRegistryOrder()
    {
        var inventory = new ClientInventory();
        // Apply out of registry order to prove ordering comes from the registry, not insertion order.
        inventory.Apply([new ItemStack("fiber", 1), new ItemStack("wood", 4)]);

        var rows = inventory.ToOrderedRows(ItemRegistry.Default);

        Assert.Equal(2, rows.Count);
        Assert.Equal("wood", rows[0].TemplateKey);
        Assert.Equal("Wood", rows[0].DisplayName);
        Assert.Equal(4, rows[0].Quantity);
        Assert.Equal("fiber", rows[1].TemplateKey);
        Assert.Equal("Fiber", rows[1].DisplayName);
    }

    [Fact]
    public void ToOrderedRowsFallsBackToKeyForUnknownItems()
    {
        var inventory = new ClientInventory();
        inventory.Apply([new ItemStack("mystery", 1)]);

        var row = Assert.Single(inventory.ToOrderedRows(ItemRegistry.Default));

        Assert.Equal("mystery", row.TemplateKey);
        Assert.Equal("mystery", row.DisplayName);
    }
}
