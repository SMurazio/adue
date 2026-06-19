using Mmo.Shared.Domain;

namespace Mmo.Server.Runtime;

// Code registry mapping a stable resource-node Key -> its immutable definition. Seeded with the three
// node types matching S37's seeded items (Tree->Wood, Rock->Stone, Plant->Fiber). Adding a node type =
// a new entry here. The default registry validates that every yield key exists in the supplied item
// registry, so a typo'd yield fails fast at startup instead of silently granting nothing.
public sealed class ResourceNodeRegistry
{
    private readonly Dictionary<string, ResourceNodeDefinition> _byKey;

    public ResourceNodeRegistry(IReadOnlyList<ResourceNodeDefinition> definitions, ItemRegistry itemRegistry)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(itemRegistry);

        _byKey = new Dictionary<string, ResourceNodeDefinition>(definitions.Count, StringComparer.Ordinal);
        foreach (var definition in definitions)
        {
            if (!itemRegistry.Contains(definition.YieldItemKey))
            {
                throw new ArgumentException(
                    $"Resource node '{definition.Key}' yields unknown item '{definition.YieldItemKey}'.",
                    nameof(definitions));
            }

            if (!_byKey.TryAdd(definition.Key, definition))
            {
                throw new ArgumentException($"Duplicate resource node key '{definition.Key}'.", nameof(definitions));
            }
        }
    }

    // Default content for the first gather loop. Respawn times are deliberately short (seconds at the
    // 20Hz default tick) so the loop is observable in tests and manual play without waiting.
    public static ResourceNodeRegistry CreateDefault(ItemRegistry itemRegistry)
    {
        return new ResourceNodeRegistry(
        [
            new ResourceNodeDefinition("tree", "Tree", YieldItemKey: "wood", YieldQuantity: 1, RespawnTicks: 100),
            new ResourceNodeDefinition("rock", "Rock", YieldItemKey: "stone", YieldQuantity: 1, RespawnTicks: 200),
            new ResourceNodeDefinition("plant", "Plant", YieldItemKey: "fiber", YieldQuantity: 1, RespawnTicks: 60),
        ],
        itemRegistry);
    }

    public IReadOnlyCollection<ResourceNodeDefinition> Definitions => _byKey.Values;

    public bool TryGet(string key, out ResourceNodeDefinition definition)
    {
        return _byKey.TryGetValue(key, out definition!);
    }

    public ResourceNodeDefinition Get(string key)
    {
        return _byKey.TryGetValue(key, out var definition)
            ? definition
            : throw new KeyNotFoundException($"Unknown resource node key '{key}'.");
    }
}
