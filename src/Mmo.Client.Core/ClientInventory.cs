using Mmo.Shared.Domain;

namespace Mmo.Client.Core;

// Client-side mirror of the player's private inventory, driven entirely by the owner-only
// InventoryUpdate deltas the server sends. It holds no authority: each InventoryUpdate carries the new
// authoritative Quantity per template (0 = the stack is now empty and is dropped), so applying a delta
// is a simple set-or-remove keyed by template. Server-agnostic and allocation-light so it can be unit
// tested and (later) reused by any client view. Display names are resolved against an ItemRegistry.
public sealed class ClientInventory
{
    private readonly Dictionary<string, int> _quantities = new(StringComparer.Ordinal);

    // Monotonic counter bumped on every applied change so a view can cheaply detect "did anything
    // change since I last rendered?" without diffing the map each frame.
    public long Version { get; private set; }

    public int DistinctItemCount => _quantities.Count;

    public int QuantityOf(string templateKey)
    {
        return _quantities.TryGetValue(templateKey, out var quantity) ? quantity : 0;
    }

    // Applies one owner-only InventoryUpdate. Each stack's Quantity is the new authoritative total for
    // that template; <= 0 removes the stack. Returns true if the visible state actually changed.
    public bool Apply(IReadOnlyList<ItemStack> changedStacks)
    {
        ArgumentNullException.ThrowIfNull(changedStacks);

        var changed = false;
        foreach (var stack in changedStacks)
        {
            if (string.IsNullOrEmpty(stack.TemplateKey))
            {
                continue;
            }

            if (stack.Quantity <= 0)
            {
                changed |= _quantities.Remove(stack.TemplateKey);
                continue;
            }

            if (!_quantities.TryGetValue(stack.TemplateKey, out var existing) || existing != stack.Quantity)
            {
                _quantities[stack.TemplateKey] = stack.Quantity;
                changed = true;
            }
        }

        if (changed)
        {
            Version++;
        }

        return changed;
    }

    public void Clear()
    {
        if (_quantities.Count == 0)
        {
            return;
        }

        _quantities.Clear();
        Version++;
    }

    // Stable, registry-ordered snapshot for display: items present in the registry come first in the
    // registry's order (so the HUD layout is steady frame to frame), then any unknown keys alphabetically.
    // Each row resolves the registry display name, falling back to the raw key for items not in the
    // registry. Allocates a list, so callers should poll on Version, not every frame.
    public IReadOnlyList<InventoryRow> ToOrderedRows(ItemRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        var rows = new List<InventoryRow>(_quantities.Count);
        foreach (var definition in registry.Definitions)
        {
            if (_quantities.TryGetValue(definition.Key, out var quantity) && quantity > 0)
            {
                rows.Add(new InventoryRow(definition.Key, definition.DisplayName, quantity));
            }
        }

        foreach (var pair in _quantities)
        {
            if (!registry.Contains(pair.Key) && pair.Value > 0)
            {
                rows.Add(new InventoryRow(pair.Key, pair.Key, pair.Value));
            }
        }

        return rows;
    }
}

public readonly record struct InventoryRow(string TemplateKey, string DisplayName, int Quantity);
