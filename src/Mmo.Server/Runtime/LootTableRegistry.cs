using Mmo.Shared.Domain;

namespace Mmo.Server.Runtime;

// LOOT P4a — the table store + the seeded roll entry point. Tables are FIRST-CLASS and SHARED: a monster
// type references one by id (MonsterType.LootTableId), many types can share a table, and tables nest other
// tables via tableRef to build shared rarity pools (add a rare to `rare_material_pool` and it drops from
// every table that nests it). Defined ONCE here.
//
// RECURSION GUARD: tableRef nesting could cycle (A→B→A) or nest pathologically deep. Roll carries a depth
// budget (MaxNestingDepth); a ref that would exceed it is dropped (logged), so a mis-authored cycle yields
// finite loot instead of a stack overflow. Validated at construction too: the default seed is acyclic and
// every referenced id exists / every resourceId exists in the item registry, so a typo fails fast at startup.
public sealed class LootTableRegistry
{
    // Depth 0 = the monster's own table; each tableRef hop costs 1. 8 is far beyond any real nesting (the
    // seed nests 1 deep) — it only exists to stop a cycle, not to limit legitimate design.
    public const int MaxNestingDepth = 8;

    private readonly Dictionary<string, LootTable> _byId;

    public LootTableRegistry(IReadOnlyList<LootTable> tables, ItemRegistry itemRegistry)
    {
        ArgumentNullException.ThrowIfNull(tables);
        ArgumentNullException.ThrowIfNull(itemRegistry);

        _byId = new Dictionary<string, LootTable>(tables.Count, StringComparer.Ordinal);
        foreach (var table in tables)
        {
            if (!_byId.TryAdd(table.Id, table))
            {
                throw new ArgumentException($"Duplicate loot table id '{table.Id}'.", nameof(tables));
            }
        }

        // Fail fast: every referenced resourceId must exist in the item registry, and every referenced
        // tableId must exist here. Catches a typo'd id at startup instead of silently dropping nothing.
        foreach (var table in _byId.Values)
        {
            ValidateReferences(table, itemRegistry);
        }
    }

    public IReadOnlyCollection<LootTable> Tables => _byId.Values;

    public bool TryGet(string id, out LootTable table) => _byId.TryGetValue(id, out table!);

    public bool Contains(string id) => _byId.ContainsKey(id);

    // The seeded roll for a table id. Resolves every drop independently, recursing through tableRefs under
    // the depth guard. An unknown/empty id => no loot (empty list). `rng` is caller-owned → deterministic.
    public IReadOnlyList<ItemStack> Roll(string? tableId, Random rng)
    {
        ArgumentNullException.ThrowIfNull(rng);
        var sink = new List<ItemStack>();
        if (string.IsNullOrEmpty(tableId) || !_byId.TryGetValue(tableId, out var table))
        {
            return sink;
        }

        RollInto(table, rng, sink, depth: 0);
        return sink;
    }

    private void RollInto(LootTable table, Random rng, List<ItemStack> sink, int depth)
    {
        table.RollInto(rng, sink, (refId, refSink) =>
        {
            if (depth + 1 > MaxNestingDepth)
            {
                // The recursion guard tripped: a cycle or runaway nesting. Drop the ref and keep going so
                // the roll still returns finite loot. Logged so a mis-authored table is visible, not silent.
                Log.Warn($"Loot table recursion guard tripped at depth {depth + 1} resolving '{refId}' " +
                         $"(from '{table.Id}'); skipping the nested ref.");
                return;
            }

            if (!_byId.TryGetValue(refId, out var nested))
            {
                // Shouldn't happen (construction validates refs), but a defensive skip beats a crash.
                Log.Warn($"Loot table '{table.Id}' refs unknown table '{refId}'; skipping.");
                return;
            }

            RollInto(nested, rng, refSink, depth + 1);
        });
    }

    private void ValidateReferences(LootTable table, ItemRegistry itemRegistry)
    {
        foreach (var drop in table.Drops)
        {
            switch (drop)
            {
                case FixedDrop fixedDrop:
                    RequireResource(table.Id, fixedDrop.ResourceId, itemRegistry);
                    break;

                case WeightedPickDrop pick:
                    foreach (var option in pick.Options)
                    {
                        if (option.IsTableRef)
                        {
                            RequireTable(table.Id, option.TableId!);
                        }
                        else
                        {
                            RequireResource(table.Id, option.ResourceId!, itemRegistry);
                        }
                    }

                    break;

                case TableRefDrop tableRef:
                    RequireTable(table.Id, tableRef.TableId);
                    break;
            }
        }
    }

    private void RequireTable(string fromTable, string refId)
    {
        if (!_byId.ContainsKey(refId))
        {
            throw new ArgumentException($"Loot table '{fromTable}' references unknown table '{refId}'.");
        }
    }

    private static void RequireResource(string fromTable, string resourceId, ItemRegistry itemRegistry)
    {
        if (!itemRegistry.Contains(resourceId))
        {
            throw new ArgumentException(
                $"Loot table '{fromTable}' references unknown resource '{resourceId}'.");
        }
    }

    // The default content. Mirrors the docs/loot-design.md example, mapped to REAL item ids:
    //
    //   rare_material_pool (weightedPick, never empty): one of the shared rare mats by weight.
    //   slime_loot:
    //     slime_gel    chance 1.00  qty 1-3          # floor — every kill gives a mat
    //     →rare_material_pool  chance ~0.4%          # the shared rare tail (nested tableRef)
    //     slime_core   chance 0.08%  qty 1           # the slime's signature chase drop
    //
    // The pool is nested (a tableRef drop gated by a 0.004 fixed chance is modelled as a weightedPick with
    // a tiny non-empty band? No — the "rare tail" wants an independent chance, so it is a FixedDrop-gated
    // tableRef): we express "→pool at 0.4%" as a WeightedPickDrop with ONE tableRef option (weight 0.004)
    // and EmptyWeight 0.996, so 0.4% of kills resolve the pool and 99.6% yield nothing from that drop.
    public static LootTableRegistry CreateDefault(ItemRegistry itemRegistry)
    {
        var rareMaterialPool = new LootTable("rare_material_pool",
        [
            // One of the shared rares by weight, never empty (EmptyWeight 0): whenever the pool is rolled,
            // it yields exactly one rare. arcane_dust (Rare) is the common-rare; crystal_shard (Epic) the
            // rarer half. Add a new rare here once and every table that nests this pool gains it.
            new WeightedPickDrop(
            [
                WeightedPickOption.Resource("arcane_dust", weight: 60, minQty: 1, maxQty: 2),
                WeightedPickOption.Resource("crystal_shard", weight: 40, minQty: 1, maxQty: 1),
            ],
            emptyWeight: 0),
        ]);

        var slimeLoot = new LootTable("slime_loot",
        [
            // Floor: every slime kill drops 1-3 gel (guaranteed = chance 1.0).
            new FixedDrop("slime_gel", chance: 1.0, minQty: 1, maxQty: 3),

            // The shared rare tail at ~0.4%: a weightedPick whose only option is the rare pool (weight
            // 0.004) against a 0.996 empty band. ~0.4% of kills splice in one pool roll; the rest yield
            // nothing from this drop. (A FixedDrop can't tableRef, so the chance-gated ref is a 1-option pick.)
            new WeightedPickDrop(
            [
                WeightedPickOption.TableRef("rare_material_pool", weight: 0.004),
            ],
            emptyWeight: 0.996),

            // The slime's signature chase mat at ~0.08% (the reason to farm slimes specifically).
            new FixedDrop("slime_core", chance: 0.0008, minQty: 1, maxQty: 1),
        ]);

        return new LootTableRegistry([rareMaterialPool, slimeLoot], itemRegistry);
    }
}
