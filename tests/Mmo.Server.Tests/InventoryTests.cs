using Mmo.Server.Runtime;
using Mmo.Shared.Domain;
using Xunit;

namespace Mmo.Server.Tests;

public sealed class InventoryTests
{
    private static readonly ItemRegistry Registry = new(
    [
        new ItemDefinition("wood", "Wood", MaxStack: 99, ItemCategory.Resource),
        new ItemDefinition("stone", "Stone", MaxStack: 10, ItemCategory.Resource),
    ]);

    [Fact]
    public void TryAddStacksSameTemplateAndReportsAmountAdded()
    {
        var inventory = new Inventory(Registry);

        Assert.Equal(3, inventory.TryAdd("wood", 3));
        Assert.Equal(2, inventory.TryAdd("wood", 2));

        Assert.Equal(5, inventory.QuantityOf("wood"));
        Assert.Equal(1, inventory.DistinctStackCount);
    }

    [Fact]
    public void TryAddHonorsMaxStackAndReturnsRemainderUnadded()
    {
        var inventory = new Inventory(Registry);

        var added = inventory.TryAdd("stone", 15);

        Assert.Equal(10, added);
        Assert.Equal(10, inventory.QuantityOf("stone"));
        Assert.Equal(0, inventory.TryAdd("stone", 5));
    }

    [Fact]
    public void TryAddIgnoresUnknownKeysAndNonPositiveQuantities()
    {
        var inventory = new Inventory(Registry);

        Assert.Equal(0, inventory.TryAdd("mithril", 5));
        Assert.Equal(0, inventory.TryAdd("wood", 0));
        Assert.Equal(0, inventory.TryAdd("wood", -3));
        Assert.Equal(0, inventory.DistinctStackCount);
    }

    [Fact]
    public void RemoveReducesQuantityAndDropsEmptyStacks()
    {
        var inventory = new Inventory(Registry);
        inventory.TryAdd("wood", 5);

        Assert.Equal(2, inventory.Remove("wood", 2));
        Assert.Equal(3, inventory.QuantityOf("wood"));

        Assert.Equal(3, inventory.Remove("wood", 10));
        Assert.Equal(0, inventory.QuantityOf("wood"));
        Assert.Equal(0, inventory.DistinctStackCount);
    }

    [Fact]
    public void RemoveReturnsZeroForAbsentItems()
    {
        var inventory = new Inventory(Registry);

        Assert.Equal(0, inventory.Remove("wood", 1));
    }

    [Fact]
    public void SnapshotReturnsCurrentStacksOrderedByKey()
    {
        var inventory = new Inventory(Registry);
        inventory.TryAdd("wood", 4);
        inventory.TryAdd("stone", 2);

        var snapshot = inventory.Snapshot();

        Assert.Equal(2, snapshot.Count);
        Assert.Equal(new ItemStack("stone", 2), snapshot[0]);
        Assert.Equal(new ItemStack("wood", 4), snapshot[1]);
    }

    [Fact]
    public void InitialStacksSeedWithoutMarkingDirty()
    {
        var inventory = new Inventory(Registry, [new ItemStack("wood", 7)]);

        Assert.Equal(7, inventory.QuantityOf("wood"));
        Assert.False(inventory.HasPendingChanges);
    }

    [Fact]
    public void InitialStacksSkipUnknownTemplatesAndCoalesceDuplicates()
    {
        var inventory = new Inventory(Registry,
        [
            new ItemStack("wood", 3),
            new ItemStack("wood", 4),
            new ItemStack("gone", 9),
        ]);

        Assert.Equal(7, inventory.QuantityOf("wood"));
        Assert.Equal(0, inventory.QuantityOf("gone"));
        Assert.Equal(1, inventory.DistinctStackCount);
    }

    [Fact]
    public void DrainDirtyKeysReportsChangesIncludingDeletesThenClears()
    {
        var inventory = new Inventory(Registry, [new ItemStack("wood", 5)]);
        inventory.TryAdd("stone", 2);
        inventory.Remove("wood", 5);

        Assert.True(inventory.HasPendingChanges);
        var changes = inventory.DrainDirtyKeys();

        Assert.Equal(2, changes.Count);
        Assert.Contains(changes, c => c.TemplateKey == "stone" && c.Quantity == 2);
        Assert.Contains(changes, c => c.TemplateKey == "wood" && c.Quantity == 0);

        Assert.False(inventory.HasPendingChanges);
        Assert.Empty(inventory.DrainDirtyKeys());
    }
}
