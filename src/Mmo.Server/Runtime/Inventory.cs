using Mmo.Shared.Domain;

namespace Mmo.Server.Runtime;

// Server-authoritative, per-character inventory. The single validated mutation path for owned items:
// every add/remove flows through TryAdd/Remove so stacking and (future) dupe-prevention live in one
// place. Pure and unit-testable; no I/O. Holds stackable resources only for now — InstanceId on
// ItemStack is a reserved seam, not yet used here.
//
// Persistence is write-behind: callers drain DrainDirtyKeys() at a checkpoint to learn which template
// keys changed since the last drain, then look up the current quantity (0 => delete row). This keeps
// the tick hot path free of DB writes (mirrors the dirty-tile pattern).
public sealed class Inventory
{
    private readonly ItemRegistry _registry;
    private readonly Dictionary<string, int> _quantities;
    private readonly HashSet<string> _dirtyKeys = new(StringComparer.Ordinal);

    public Inventory(ItemRegistry registry)
        : this(registry, [])
    {
    }

    // Used when loading from persistence: seeds quantities without marking anything dirty, so a freshly
    // loaded inventory does not immediately re-persist what we just read.
    public Inventory(ItemRegistry registry, IEnumerable<ItemStack> initialStacks)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _quantities = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var stack in initialStacks)
        {
            if (stack.Quantity <= 0)
            {
                continue;
            }

            if (!_registry.Contains(stack.TemplateKey))
            {
                // Defensive: skip rows whose template no longer exists in the registry rather than crash
                // a login. They simply don't load.
                continue;
            }

            _quantities[stack.TemplateKey] = _quantities.GetValueOrDefault(stack.TemplateKey) + stack.Quantity;
        }
    }

    public int DistinctStackCount => _quantities.Count;

    // Snapshot of current non-empty stacks, ordered by key for deterministic output.
    public IReadOnlyList<ItemStack> Snapshot()
    {
        var stacks = new List<ItemStack>(_quantities.Count);
        foreach (var pair in _quantities)
        {
            stacks.Add(new ItemStack(pair.Key, pair.Value));
        }

        stacks.Sort(static (a, b) => string.CompareOrdinal(a.TemplateKey, b.TemplateKey));
        return stacks;
    }

    public int QuantityOf(string templateKey)
    {
        return _quantities.GetValueOrDefault(templateKey);
    }

    // Adds up to `quantity` of an item, honoring MaxStack as a per-template cap (stacks of the same
    // template coalesce into one logical count, capped at MaxStack). Returns the amount actually added;
    // the remainder (e.g. when the cap is hit) is reported so callers can decide what to do. Unknown
    // keys or non-positive quantities add nothing and return 0.
    public int TryAdd(string templateKey, int quantity)
    {
        if (quantity <= 0 || !_registry.TryGet(templateKey, out var definition))
        {
            return 0;
        }

        var current = _quantities.GetValueOrDefault(templateKey);
        var capacityRemaining = definition.MaxStack - current;
        if (capacityRemaining <= 0)
        {
            return 0;
        }

        var added = Math.Min(quantity, capacityRemaining);
        _quantities[templateKey] = current + added;
        _dirtyKeys.Add(templateKey);
        return added;
    }

    // Removes up to `quantity` of an item. Returns the amount actually removed (0 if absent). Emptied
    // stacks are dropped from the map so Snapshot/persistence treat them as deletes.
    public int Remove(string templateKey, int quantity)
    {
        if (quantity <= 0 || !_quantities.TryGetValue(templateKey, out var current))
        {
            return 0;
        }

        var removed = Math.Min(quantity, current);
        var remaining = current - removed;
        if (remaining <= 0)
        {
            _quantities.Remove(templateKey);
        }
        else
        {
            _quantities[templateKey] = remaining;
        }

        _dirtyKeys.Add(templateKey);
        return removed;
    }

    public bool HasPendingChanges => _dirtyKeys.Count > 0;

    // Returns the set of template keys whose quantity changed since the last drain, pairing each with
    // its current quantity (0 => the row should be deleted), and clears the dirty set. Caller persists
    // these and is responsible for completing the write.
    public IReadOnlyList<ItemStack> DrainDirtyKeys()
    {
        if (_dirtyKeys.Count == 0)
        {
            return [];
        }

        var changes = new List<ItemStack>(_dirtyKeys.Count);
        foreach (var key in _dirtyKeys)
        {
            changes.Add(new ItemStack(key, _quantities.GetValueOrDefault(key)));
        }

        _dirtyKeys.Clear();
        return changes;
    }
}
