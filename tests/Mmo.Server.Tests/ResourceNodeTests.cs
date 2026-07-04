using Mmo.Server.Runtime;
using Mmo.Shared.Domain;
using Xunit;

namespace Mmo.Server.Tests;

// NODE-FIELD N2: the per-instance ResourceNode (available/depleted + respawn timer) and the WorldEntity
// depletion plumbing this file used to also pin were RETIRED along with the entity harvest path — that
// mutable per-index state now lives in NodeField (see NodeFieldTests). ResourceNodeRegistry/
// ResourceNodeDefinition survive UNCHANGED (N2 reuses them, keyed by NodeType, for the per-type yield/
// respawn-ticks content the harvest flow awards) — this file keeps just their coverage.
public sealed class ResourceNodeTests
{
    [Fact]
    public void RegistryRejectsYieldWithUnknownItem()
    {
        var items = new ItemRegistry([new ItemDefinition("wood", "Wood", 99, ItemCategory.Resource)]);

        Assert.Throws<ArgumentException>(() => new ResourceNodeRegistry(
            [new ResourceNodeDefinition("rock", "Rock", "stone", 1, 50)],
            items));
    }

    [Fact]
    public void DefaultRegistrySeedsTypesMatchingItemRegistry()
    {
        var registry = ResourceNodeRegistry.CreateDefault(ItemRegistry.Default);

        Assert.True(registry.TryGet("tree", out var tree));
        Assert.Equal("wood", tree.YieldItemKey);
        Assert.True(registry.TryGet("rock", out var rock));
        Assert.Equal("stone", rock.YieldItemKey);
        Assert.True(registry.TryGet("plant", out var plant));
        Assert.Equal("fiber", plant.YieldItemKey);
    }
}
