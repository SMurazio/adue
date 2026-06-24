using Mmo.Shared.Domain;

namespace Mmo.Client.Core;

// LOOT P4c: the client-side mirror of an OPEN corpse's loot-window contents, driven entirely by the owner-only
// CorpseContentsMessage. It holds no authority — each replication carries the full current contents (the window is
// stateless; the server is the truth) — so it is just the corpse's network id plus the rarity-tagged rows the panel
// lists. Mirrors ClientInventory's shape: the wire ships TemplateKey + Quantity + Rarity, and the DisplayName is
// resolved at render time against an ItemRegistry (falling back to the raw key for an unknown template). Immutable
// snapshot; a new one replaces it on every refresh, so the Godot panel polls MmoClient.CorpseLootVersion.
public sealed class ClientCorpseLoot
{
    private readonly IReadOnlyList<CorpseItem> _items;

    private ClientCorpseLoot(uint corpseNetworkId, IReadOnlyList<CorpseItem> items)
    {
        CorpseNetworkId = corpseNetworkId;
        _items = items;
    }

    // The network id of the corpse this window belongs to — the id the loot-action sends carry so a stale window can't
    // loot a different corpse.
    public uint CorpseNetworkId { get; }

    public int ItemCount => _items.Count;

    public static ClientCorpseLoot From(uint corpseNetworkId, IReadOnlyList<CorpseItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return new ClientCorpseLoot(corpseNetworkId, items);
    }

    // Display rows for the loot panel: each item's template key, the registry display name (falling back to the raw
    // key for an unknown template), quantity, and rarity (for the row colour). Registry-agnostic input order is kept
    // (the server sends contents ordered by template key), so the panel layout is steady across refreshes.
    public IReadOnlyList<CorpseLootRow> ToRows(ItemRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        var rows = new List<CorpseLootRow>(_items.Count);
        foreach (var item in _items)
        {
            var displayName = registry.TryGet(item.TemplateKey, out var definition) ? definition.DisplayName : item.TemplateKey;
            rows.Add(new CorpseLootRow(item.TemplateKey, displayName, item.Quantity, item.Rarity));
        }

        return rows;
    }
}

public readonly record struct CorpseLootRow(string TemplateKey, string DisplayName, int Quantity, Rarity Rarity);
