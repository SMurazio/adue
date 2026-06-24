using Mmo.Shared.Domain;

namespace Mmo.Server.Runtime;

// LOOT P4a — the drop model. A LootTable is an ordered list of LootDrops, each resolved INDEPENDENTLY
// (the "predictable floor + a rare tail" from docs/loot-design.md). A drop is one of three shapes,
// modelled as sealed subclasses of an abstract base (a closed discriminated hierarchy — the roll
// switches over the concrete type; adding a shape is a new subclass + a new switch arm, no flag soup):
//
//   FixedDrop        { resourceId, chance, minQty, maxQty } — the guaranteed/chance floor. Rolls a
//                      single Bernoulli(chance); on success emits resourceId × U[minQty, maxQty].
//                      chance 1.0 = guaranteed. This is what makes "every kill gives something".
//   WeightedPickDrop — rolls ONCE and picks exactly ONE option by weight, where the options include an
//                      explicit empty/"no-drop" weight (so a pick can yield nothing). An option is a
//                      resource (resourceId × qty range) OR a nested tableRef (resolve another table).
//   TableRefDrop     { tableId } — resolve another LootTable by id and splice its stacks in. Nesting is
//                      how shared rarity pools work: add a rare to one pool and it drops from every table
//                      that refs it. Guarded against cycles/runaway depth by LootTableRegistry.
//
// Each shape is a plain immutable value; the resolution lives in LootTable.Roll so the model stays data.
public abstract class LootDrop
{
    // Sealed so the roll's type switch is exhaustive; external code can't add an unhandled shape.
    private protected LootDrop()
    {
    }
}

// A single resourceId with an independent drop chance and an inclusive quantity range. chance is a
// probability in [0,1]; 1.0 = a guaranteed drop. minQty/maxQty are an inclusive range (min==max = fixed).
public sealed class FixedDrop : LootDrop
{
    public FixedDrop(string resourceId, double chance, int minQty, int maxQty)
    {
        if (string.IsNullOrWhiteSpace(resourceId))
        {
            throw new ArgumentException("FixedDrop requires a non-empty resourceId.", nameof(resourceId));
        }

        if (!double.IsFinite(chance) || chance < 0d || chance > 1d)
        {
            throw new ArgumentOutOfRangeException(nameof(chance), chance, "chance must be in [0, 1].");
        }

        if (minQty < 1 || maxQty < minQty)
        {
            throw new ArgumentOutOfRangeException(nameof(maxQty), $"Invalid qty range {minQty}..{maxQty}.");
        }

        ResourceId = resourceId;
        Chance = chance;
        MinQty = minQty;
        MaxQty = maxQty;
    }

    public string ResourceId { get; }
    public double Chance { get; }
    public int MinQty { get; }
    public int MaxQty { get; }
}

// One option inside a WeightedPickDrop: a positive Weight plus EITHER a resource (ResourceId + qty range)
// OR a nested table (TableId). Exactly one of the two is set; the "no-drop" outcome is a separate empty
// weight on the drop itself, not an option here.
public sealed class WeightedPickOption
{
    private WeightedPickOption(double weight, string? resourceId, int minQty, int maxQty, string? tableId)
    {
        if (!double.IsFinite(weight) || weight <= 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(weight), weight, "Option weight must be > 0.");
        }

        Weight = weight;
        ResourceId = resourceId;
        MinQty = minQty;
        MaxQty = maxQty;
        TableId = tableId;
    }

    public double Weight { get; }

    // Set iff this option is a resource. Null for a tableRef option.
    public string? ResourceId { get; }
    public int MinQty { get; }
    public int MaxQty { get; }

    // Set iff this option resolves a nested table. Null for a resource option.
    public string? TableId { get; }

    public bool IsTableRef => TableId is not null;

    public static WeightedPickOption Resource(string resourceId, double weight, int minQty, int maxQty)
    {
        if (string.IsNullOrWhiteSpace(resourceId))
        {
            throw new ArgumentException("Resource option requires a non-empty resourceId.", nameof(resourceId));
        }

        if (minQty < 1 || maxQty < minQty)
        {
            throw new ArgumentOutOfRangeException(nameof(maxQty), $"Invalid qty range {minQty}..{maxQty}.");
        }

        return new WeightedPickOption(weight, resourceId, minQty, maxQty, tableId: null);
    }

    public static WeightedPickOption TableRef(string tableId, double weight)
    {
        if (string.IsNullOrWhiteSpace(tableId))
        {
            throw new ArgumentException("TableRef option requires a non-empty tableId.", nameof(tableId));
        }

        return new WeightedPickOption(weight, resourceId: null, minQty: 0, maxQty: 0, tableId);
    }
}

// Rolls ONCE and picks one option by weight, including an explicit EmptyWeight ("no drop"). The picked
// option is either a resource or a nested table. EmptyWeight may be 0 (every roll yields an option).
public sealed class WeightedPickDrop : LootDrop
{
    public WeightedPickDrop(IReadOnlyList<WeightedPickOption> options, double emptyWeight)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Count == 0)
        {
            throw new ArgumentException("WeightedPickDrop requires at least one option.", nameof(options));
        }

        if (!double.IsFinite(emptyWeight) || emptyWeight < 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(emptyWeight), emptyWeight, "emptyWeight must be >= 0.");
        }

        Options = options;
        EmptyWeight = emptyWeight;
        TotalWeight = emptyWeight;
        foreach (var option in options)
        {
            TotalWeight += option.Weight;
        }
    }

    public IReadOnlyList<WeightedPickOption> Options { get; }
    public double EmptyWeight { get; }

    // Cached sum (all option weights + EmptyWeight); always > 0 since every option weight is > 0.
    public double TotalWeight { get; }
}

// Resolves another table by id and splices its rolled stacks in. The cycle/depth guard lives in the
// registry's Roll (a TableRef can't, by itself, see the recursion budget).
public sealed class TableRefDrop : LootDrop
{
    public TableRefDrop(string tableId)
    {
        if (string.IsNullOrWhiteSpace(tableId))
        {
            throw new ArgumentException("TableRefDrop requires a non-empty tableId.", nameof(tableId));
        }

        TableId = tableId;
    }

    public string TableId { get; }
}
