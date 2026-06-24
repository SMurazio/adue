using Mmo.Shared.Domain;

namespace Mmo.Server.Runtime;

// LOOT P4b — the server-side loot payload behind a Corpse world entity. The Corpse ENTITY (EntityKind.Corpse) is a
// transient WorldEntity that AOI-replicates + renders + is interactable through the existing paths; THIS object is
// the server-only state hanging off it (the contents NEVER replicate this phase — P4c adds the loot-window). It
// holds:
//   * Contents      — the rolled ItemStacks (the P4a roll), mutated as items are looted out.
//   * EligibleLooters — the durable CharacterIds the kill's contribution ledger produced (solo = the killer).
//   * Mode          — how loot is distributed (FfaAmongEligible in P4b; the tag is here for the later modes).
//   * DecayAtTick   — the server tick at/after which the corpse despawns even if unlooted.
//
// The loot-all transfer is a PURE method (TryLootAll) so it is unit-testable against an Inventory without a live
// server: it gates eligibility, transfers every stack via Inventory.TryAdd (honouring stack caps), and leaves any
// un-added remainder IN the corpse rather than vanishing it (a full/partial inventory is graceful). The GameServer
// owns only the entity spawn/despawn + interact wiring around it.
public sealed class Corpse
{
    private readonly List<ItemStack> _contents;
    private readonly HashSet<Guid> _eligibleLooters;

    public Corpse(
        ulong entityId,
        IEnumerable<ItemStack> contents,
        IEnumerable<Guid> eligibleLooters,
        LootMode mode,
        uint decayAtTick)
    {
        EntityId = entityId;
        Mode = mode;
        DecayAtTick = decayAtTick;
        _eligibleLooters = [.. eligibleLooters];
        // Copy + coalesce: keep only positive stacks, merge duplicate keys so the corpse holds one stack per
        // template (the loot roll can emit two bands of the same resource — e.g. a floor + a pool hit).
        _contents = [];
        foreach (var stack in contents)
        {
            if (stack.Quantity <= 0)
            {
                continue;
            }

            var existing = _contents.FindIndex(s => s.TemplateKey == stack.TemplateKey);
            if (existing >= 0)
            {
                _contents[existing] = _contents[existing].WithQuantity(_contents[existing].Quantity + stack.Quantity);
            }
            else
            {
                _contents.Add(stack);
            }
        }
    }

    // The entity id of the Corpse WorldEntity this payload belongs to (the key the GameServer maps a corpse network
    // id / interact target back to its loot).
    public ulong EntityId { get; }

    public LootMode Mode { get; }

    // The server tick at/after which a decay pass despawns this corpse even if it still holds loot.
    public uint DecayAtTick { get; }

    // Snapshot of the remaining stacks (for tests / a future loot-window). Ordered by key for determinism.
    public IReadOnlyList<ItemStack> Contents
    {
        get
        {
            var snapshot = new List<ItemStack>(_contents);
            snapshot.Sort(static (a, b) => string.CompareOrdinal(a.TemplateKey, b.TemplateKey));
            return snapshot;
        }
    }

    public bool IsEmpty => _contents.Count == 0;

    // True iff `looterId` is in the eligible-looter set (the contribution ledger's contributors). The interact path
    // gates loot on this — a non-eligible player is rejected and the loot is untouched.
    public bool IsEligible(Guid looterId) => _eligibleLooters.Contains(looterId);

    // True iff this corpse's decay deadline has been reached (a per-tick pass despawns it).
    public bool IsDecayed(uint serverTick) => serverTick >= DecayAtTick;

    // The result of a loot-all attempt: whether anything moved, the stacks actually transferred into the looter's
    // inventory (for the feedback toast), and whether the corpse is now empty (so the caller despawns it).
    public readonly record struct LootAllResult(bool Looted, IReadOnlyList<ItemStack> Transferred, bool CorpseEmptied);

    // LOOT P4c: the result of a single-stack take. Took = whether ANY of the named stack moved (false if the key
    // isn't in the corpse or none of it fit); Transferred = how much actually moved (for the toast / inventory
    // delta); CorpseEmptied = whether the corpse is now empty (so the caller despawns it instantly).
    public readonly record struct TakeItemResult(bool Took, ItemStack Transferred, bool CorpseEmptied);

    // LOOT P4c: take the SINGLE stack identified by `templateKey` into `inventory` via Inventory.TryAdd (stack-cap
    // honouring; an un-added remainder stays in the corpse — nothing vanishes, same as TryLootAll). The window's
    // per-item take button calls this. Eligibility is NOT re-checked here (the caller gates it, like TryLootAll), so
    // this stays a pure transfer primitive. An unknown key, an absent stack, or a full inventory returns Took=false
    // with the corpse untouched. A take that empties the corpse reports CorpseEmptied so the caller despawns it.
    public TakeItemResult TryTakeItem(string templateKey, Inventory inventory)
    {
        ArgumentNullException.ThrowIfNull(inventory);

        if (string.IsNullOrEmpty(templateKey))
        {
            return new TakeItemResult(false, default, _contents.Count == 0);
        }

        var index = _contents.FindIndex(s => s.TemplateKey == templateKey);
        if (index < 0)
        {
            return new TakeItemResult(false, default, _contents.Count == 0);
        }

        var stack = _contents[index];
        var added = inventory.TryAdd(stack.TemplateKey, stack.Quantity);
        if (added <= 0)
        {
            // Inventory full for this template (or unknown key): leave it whole in the corpse.
            return new TakeItemResult(false, default, _contents.Count == 0);
        }

        var remaining = stack.Quantity - added;
        if (remaining <= 0)
        {
            _contents.RemoveAt(index);
        }
        else
        {
            _contents[index] = stack.WithQuantity(remaining);
        }

        return new TakeItemResult(true, new ItemStack(stack.TemplateKey, added), _contents.Count == 0);
    }

    // Transfers every stack into `inventory` via Inventory.TryAdd (which honours per-template stack caps and returns
    // the amount actually added). Items that don't fit (a full/partial inventory) are LEFT in the corpse with their
    // remaining quantity — nothing is vanished. Returns what actually moved + whether the corpse is now empty.
    //
    // Eligibility is NOT re-checked here (the caller gates it) so this stays a pure transfer primitive; the GameServer
    // checks IsEligible before calling. A corpse that ends up empty is despawned by the caller.
    public LootAllResult TryLootAll(Inventory inventory)
    {
        ArgumentNullException.ThrowIfNull(inventory);

        var transferred = new List<ItemStack>();
        // Iterate a copy of the keys: we mutate _contents in place (remove emptied stacks / reduce partials).
        for (var i = _contents.Count - 1; i >= 0; i--)
        {
            var stack = _contents[i];
            var added = inventory.TryAdd(stack.TemplateKey, stack.Quantity);
            if (added <= 0)
            {
                // None of this stack fit (unknown key or cap reached): leave it whole in the corpse.
                continue;
            }

            transferred.Add(new ItemStack(stack.TemplateKey, added));

            var remaining = stack.Quantity - added;
            if (remaining <= 0)
            {
                _contents.RemoveAt(i);
            }
            else
            {
                _contents[i] = stack.WithQuantity(remaining);
            }
        }

        if (transferred.Count == 0)
        {
            return new LootAllResult(false, transferred, _contents.Count == 0);
        }

        transferred.Sort(static (a, b) => string.CompareOrdinal(a.TemplateKey, b.TemplateKey));
        return new LootAllResult(true, transferred, _contents.Count == 0);
    }
}
