namespace Mmo.Shared.Domain;

// Code registry mapping a stable item Key -> its immutable definition. Seeded with a tiny set of
// stackable resources for the first gather loop. Adding an item = a new entry in Definitions; no
// branching logic elsewhere. Keys are case-sensitive and are the persisted/serialized identity.
public sealed class ItemRegistry
{
    public static readonly ItemRegistry Default = new(
    [
        new ItemDefinition("wood", "Wood", MaxStack: 99, ItemCategory.Resource),
        new ItemDefinition("stone", "Stone", MaxStack: 99, ItemCategory.Resource),
        new ItemDefinition("fiber", "Fiber", MaxStack: 99, ItemCategory.Resource),
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
