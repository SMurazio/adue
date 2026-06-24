using Mmo.Shared.Domain;

namespace Mmo.Server.Runtime;

// LOOT P4a — a loot table: an ordered list of LootDrops resolved INDEPENDENTLY into resource stacks.
// The roll is SEEDED (the caller owns the Random) so a given seed replays the same loot — the whole
// point of P4a's headless determinism. Output is a list of ItemStack (the existing {TemplateKey, Qty}
// value the inventory/persistence already speak; P4b's corpse consumes it directly — no parallel
// "ResourceStack" type invented).
//
// tableRef recursion is NOT resolved here — a table can't see the recursion budget. LootTableRegistry.Roll
// owns the depth/cycle guard and passes a `resolveRef` callback in; Roll appends each ref's stacks.
public sealed class LootTable
{
    public LootTable(string id, IReadOnlyList<LootDrop> drops)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("LootTable requires a non-empty id.", nameof(id));
        }

        ArgumentNullException.ThrowIfNull(drops);
        Id = id;
        Drops = drops;
    }

    public string Id { get; }
    public IReadOnlyList<LootDrop> Drops { get; }

    // Resolves every drop independently into `sink`. `rng` is the seeded source (caller-owned → deterministic).
    // `resolveRef(tableId, sink)` resolves a nested table into the SAME sink under the registry's depth guard.
    internal void RollInto(Random rng, List<ItemStack> sink, Action<string, List<ItemStack>> resolveRef)
    {
        foreach (var drop in Drops)
        {
            switch (drop)
            {
                case FixedDrop fixedDrop:
                    if (rng.NextDouble() < fixedDrop.Chance)
                    {
                        sink.Add(new ItemStack(fixedDrop.ResourceId, RollQty(rng, fixedDrop.MinQty, fixedDrop.MaxQty)));
                    }

                    break;

                case WeightedPickDrop pick:
                    ResolveWeightedPick(rng, pick, sink, resolveRef);
                    break;

                case TableRefDrop tableRef:
                    resolveRef(tableRef.TableId, sink);
                    break;

                default:
                    throw new InvalidOperationException($"Unknown loot drop shape: {drop.GetType().Name}.");
            }
        }
    }

    // Rolls once over [option weights | EmptyWeight]. A pick into the empty band yields nothing; a resource
    // option emits its stack; a tableRef option recurses via resolveRef.
    private static void ResolveWeightedPick(
        Random rng,
        WeightedPickDrop pick,
        List<ItemStack> sink,
        Action<string, List<ItemStack>> resolveRef)
    {
        var roll = rng.NextDouble() * pick.TotalWeight;
        foreach (var option in pick.Options)
        {
            if (roll < option.Weight)
            {
                if (option.IsTableRef)
                {
                    resolveRef(option.TableId!, sink);
                }
                else
                {
                    sink.Add(new ItemStack(option.ResourceId!, RollQty(rng, option.MinQty, option.MaxQty)));
                }

                return;
            }

            roll -= option.Weight;
        }

        // Fell through every option => landed in the EmptyWeight band: no drop.
    }

    // Inclusive U[min, max]. Random.Next's upper bound is exclusive, hence max + 1.
    private static int RollQty(Random rng, int minQty, int maxQty) =>
        minQty == maxQty ? minQty : rng.Next(minQty, maxQty + 1);
}
