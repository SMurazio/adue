namespace Mmo.Shared.Domain;

// Code registry mapping a stable item Key -> its immutable definition. Seeded with a tiny set of
// stackable resources for the first gather loop. Adding an item = a new entry in Definitions; no
// branching logic elsewhere. Keys are case-sensitive and are the persisted/serialized identity.
public sealed class ItemRegistry
{
    public static readonly ItemRegistry Default = new(
    [
        // Gather-loop staples (S37). No rarity concept existed before LOOT P4a, so these default to Common.
        new ItemDefinition("wood", "Wood", MaxStack: 99, ItemCategory.Resource),
        new ItemDefinition("stone", "Stone", MaxStack: 99, ItemCategory.Resource),
        new ItemDefinition("fiber", "Fiber", MaxStack: 99, ItemCategory.Resource),

        // LOOT P4a: monster-drop materials. The existing set is thin (only gather staples), so a small
        // rare tail is added here as REAL resource ids the loot tables reference. These are crafting mats
        // (the future crafting sink) — no gear yet. Rarities set so the loot window (P4c) can colour them.
        // "slime_gel" is the slime's common floor mat; "arcane_dust"/"crystal_shard" form the shared rare
        // pool; "slime_core" is the slime's signature chase drop.
        new ItemDefinition("slime_gel", "Slime Gel", MaxStack: 99, ItemCategory.Resource, Rarity.Common),
        new ItemDefinition("arcane_dust", "Arcane Dust", MaxStack: 99, ItemCategory.Resource, Rarity.Rare),
        new ItemDefinition("crystal_shard", "Crystal Shard", MaxStack: 99, ItemCategory.Resource, Rarity.Epic),
        new ItemDefinition("slime_core", "Slime Core", MaxStack: 99, ItemCategory.Resource, Rarity.Legendary),
    ]);

    private readonly Dictionary<string, ItemDefinition> _byKey;

    public ItemRegistry(IReadOnlyList<ItemDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);

        _byKey = new Dictionary<string, ItemDefinition>(definitions.Count, StringComparer.Ordinal);
        foreach (var definition in definitions)
        {
            if (string.IsNullOrWhiteSpace(definition.Key))
            {
                throw new ArgumentException("Item definitions must have a non-empty key.", nameof(definitions));
            }

            if (definition.MaxStack < 1)
            {
                throw new ArgumentException($"Item '{definition.Key}' must have MaxStack >= 1.", nameof(definitions));
            }

            if (!_byKey.TryAdd(definition.Key, definition))
            {
                throw new ArgumentException($"Duplicate item key '{definition.Key}'.", nameof(definitions));
            }
        }
    }

    public IReadOnlyCollection<ItemDefinition> Definitions => _byKey.Values;

    public bool TryGet(string key, out ItemDefinition definition)
    {
        return _byKey.TryGetValue(key, out definition!);
    }

    public ItemDefinition Get(string key)
    {
        return _byKey.TryGetValue(key, out var definition)
            ? definition
            : throw new KeyNotFoundException($"Unknown item key '{key}'.");
    }

    public bool Contains(string key)
    {
        return _byKey.ContainsKey(key);
    }
}
