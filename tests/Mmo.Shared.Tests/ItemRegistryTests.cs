using Mmo.Shared.Domain;
using Xunit;

namespace Mmo.Shared.Tests;

public sealed class ItemRegistryTests
{
    [Fact]
    public void DefaultRegistrySeedsStackableResources()
    {
        var registry = ItemRegistry.Default;

        foreach (var key in new[] { "wood", "stone", "fiber" })
        {
            Assert.True(registry.TryGet(key, out var definition));
            Assert.Equal(key, definition.Key);
            Assert.Equal(99, definition.MaxStack);
            Assert.Equal(ItemCategory.Resource, definition.Category);
            Assert.True(definition.IsStackable);
        }
    }

    [Fact]
    public void GetThrowsForUnknownKey()
    {
        Assert.Throws<KeyNotFoundException>(() => ItemRegistry.Default.Get("does-not-exist"));
        Assert.False(ItemRegistry.Default.TryGet("does-not-exist", out _));
        Assert.False(ItemRegistry.Default.Contains("does-not-exist"));
    }

    [Fact]
    public void ConstructorRejectsDuplicateKeys()
    {
        Assert.Throws<ArgumentException>(() => new ItemRegistry(
        [
            new ItemDefinition("dup", "First", 10, ItemCategory.Resource),
            new ItemDefinition("dup", "Second", 10, ItemCategory.Resource),
        ]));
    }

    [Fact]
    public void ConstructorRejectsInvalidMaxStack()
    {
        Assert.Throws<ArgumentException>(() => new ItemRegistry(
        [
            new ItemDefinition("bad", "Bad", 0, ItemCategory.Resource),
        ]));
    }

    [Fact]
    public void ConstructorRejectsEmptyKey()
    {
        Assert.Throws<ArgumentException>(() => new ItemRegistry(
        [
            new ItemDefinition(" ", "Blank", 10, ItemCategory.Resource),
        ]));
    }

    [Fact]
    public void ItemStackReservesInstanceIdSeamUnusedByDefault()
    {
        var stack = new ItemStack("wood", 5);

        Assert.Null(stack.InstanceId);
        Assert.Equal(7, stack.WithQuantity(7).Quantity);
    }
}
