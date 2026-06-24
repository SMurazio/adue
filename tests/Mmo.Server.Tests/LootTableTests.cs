using System;
using System.Collections.Generic;
using Mmo.Server.Runtime;
using Mmo.Shared.Domain;
using Xunit;

namespace Mmo.Server.Tests;

// LOOT P4a — headless, deterministic coverage of the loot ENGINE (the whole point of P4a: seeded rolls are
// reproducible, so distributions are testable without a live client). Asserts: the guaranteed floor always
// drops; weightedPick distribution (incl. the empty/no-drop band) roughly matches weights; a nested tableRef
// resolves a pool's item; qty ranges stay within min..max; a rare-tail entry drops at ~its rate over many
// seeded rolls; an empty/missing table yields no loot; the recursion guard holds (a cycle yields finite loot);
// rolls are deterministic for a given seed. Statistical asserts use a wide tolerance + a fixed seed, so they
// are reproducible and won't flake.
public sealed class LootTableTests
{
    // The item registry the loot tables validate against (real ids: slime_gel/arcane_dust/crystal_shard/slime_core).
    private static ItemRegistry Items => ItemRegistry.Default;

    private static LootTableRegistry DefaultRegistry() => LootTableRegistry.CreateDefault(Items);

    // ---- The default seed: slime_loot floor + the nested rare pool ---------------------------------------

    [Fact]
    public void GuaranteedFloorAlwaysDrops()
    {
        var registry = DefaultRegistry();

        // Over many seeded rolls, slime_gel (chance 1.0) must appear in EVERY roll.
        var rng = new Random(12345);
        for (var i = 0; i < 5000; i++)
        {
            var stacks = registry.Roll("slime_loot", rng);
            Assert.Contains(stacks, s => s.TemplateKey == "slime_gel");
        }
    }

    [Fact]
    public void FloorQuantityStaysWithinRange()
    {
        var registry = DefaultRegistry();
        var rng = new Random(999);

        for (var i = 0; i < 5000; i++)
        {
            foreach (var stack in registry.Roll("slime_loot", rng))
            {
                if (stack.TemplateKey == "slime_gel")
                {
                    Assert.InRange(stack.Quantity, 1, 3); // FixedDrop("slime_gel", qty 1..3)
                }
            }
        }
    }

    [Fact]
    public void RareTailDropsAtApproximatelyItsRate()
    {
        // slime_loot's rare tail is a weightedPick: tableRef weight 0.004 vs empty 0.996 => ~0.4% of rolls
        // splice in one rare_material_pool roll (arcane_dust OR crystal_shard). Over 200k seeded rolls the
        // observed rare-pool rate should sit near 0.4% (wide tolerance so it can't flake on a fixed seed).
        var registry = DefaultRegistry();
        var rng = new Random(2026);
        const int rolls = 200_000;
        var rares = 0;

        for (var i = 0; i < rolls; i++)
        {
            foreach (var stack in registry.Roll("slime_loot", rng))
            {
                if (stack.TemplateKey is "arcane_dust" or "crystal_shard")
                {
                    rares++;
                }
            }
        }

        var rate = rares / (double)rolls;
        // Expected 0.004; tolerance ±0.0015 absorbs sampling noise at this N for the fixed seed.
        Assert.InRange(rate, 0.004 - 0.0015, 0.004 + 0.0015);
    }

    [Fact]
    public void NestedTableRefResolvesPoolItems()
    {
        // Roll the pool directly (it's a registered table) many times; every roll must yield exactly one of
        // its two members (it has EmptyWeight 0 — never empty), proving the nested pool resolves.
        var registry = DefaultRegistry();
        var rng = new Random(77);
        var sawArcane = false;
        var sawCrystal = false;

        for (var i = 0; i < 5000; i++)
        {
            var stacks = registry.Roll("rare_material_pool", rng);
            Assert.Single(stacks);
            var key = stacks[0].TemplateKey;
            Assert.True(key is "arcane_dust" or "crystal_shard");
            sawArcane |= key == "arcane_dust";
            sawCrystal |= key == "crystal_shard";
        }

        Assert.True(sawArcane, "arcane_dust never dropped from the pool.");
        Assert.True(sawCrystal, "crystal_shard never dropped from the pool.");
    }

    // ---- Synthetic tables: weightedPick distribution + qty ranges ----------------------------------------

    [Fact]
    public void WeightedPickDistributionRoughlyMatchesWeightsIncludingEmpty()
    {
        // Three resource options (weights 50/30/20) + an empty weight of 100. Total = 200, so the expected
        // shares are a=25%, b=15%, c=10%, empty=50%. Over many rolls each observed share should land near it.
        var table = new LootTable("dist", new List<LootDrop>
        {
            new WeightedPickDrop(new List<WeightedPickOption>
            {
                WeightedPickOption.Resource("wood", weight: 50, minQty: 1, maxQty: 1),
                WeightedPickOption.Resource("stone", weight: 30, minQty: 1, maxQty: 1),
                WeightedPickOption.Resource("fiber", weight: 20, minQty: 1, maxQty: 1),
            },
            emptyWeight: 100),
        });
        var registry = new LootTableRegistry(new[] { table }, Items);

        var rng = new Random(4242);
        const int rolls = 100_000;
        var counts = new Dictionary<string, int> { ["wood"] = 0, ["stone"] = 0, ["fiber"] = 0 };
        var empty = 0;

        for (var i = 0; i < rolls; i++)
        {
            var stacks = registry.Roll("dist", rng);
            if (stacks.Count == 0)
            {
                empty++;
                continue;
            }

            Assert.Single(stacks); // a weightedPick yields exactly one option
            counts[stacks[0].TemplateKey]++;
        }

        Assert.InRange(counts["wood"] / (double)rolls, 0.25 - 0.02, 0.25 + 0.02);
        Assert.InRange(counts["stone"] / (double)rolls, 0.15 - 0.02, 0.15 + 0.02);
        Assert.InRange(counts["fiber"] / (double)rolls, 0.10 - 0.02, 0.10 + 0.02);
        Assert.InRange(empty / (double)rolls, 0.50 - 0.02, 0.50 + 0.02);
    }

    [Fact]
    public void FixedDropQuantityRangeIsInclusiveAndBounded()
    {
        var table = new LootTable("qty", new List<LootDrop>
        {
            new FixedDrop("wood", chance: 1.0, minQty: 2, maxQty: 5),
        });
        var registry = new LootTableRegistry(new[] { table }, Items);

        var rng = new Random(8);
        var sawMin = false;
        var sawMax = false;
        for (var i = 0; i < 5000; i++)
        {
            var stacks = registry.Roll("qty", rng);
            Assert.Single(stacks);
            var qty = stacks[0].Quantity;
            Assert.InRange(qty, 2, 5);
            sawMin |= qty == 2;
            sawMax |= qty == 5;
        }

        // Both endpoints reachable => the range is genuinely inclusive [min, max].
        Assert.True(sawMin && sawMax, "qty range did not reach both endpoints.");
    }

    // ---- Empty / missing tables --------------------------------------------------------------------------

    [Fact]
    public void EmptyOrUnknownTableYieldsNoLoot()
    {
        var registry = DefaultRegistry();
        var rng = new Random(1);

        Assert.Empty(registry.Roll(null, rng));
        Assert.Empty(registry.Roll(string.Empty, rng));
        Assert.Empty(registry.Roll("does_not_exist", rng));
    }

    // ---- Recursion guard ---------------------------------------------------------------------------------

    [Fact]
    public void RecursionGuardYieldsFiniteLootForACycle()
    {
        // Build a deliberate cycle a -> b -> a (each also drops one wood so we can count hops). Construction
        // doesn't reject a cycle (refs exist), but Roll's depth guard must terminate it, returning finite loot
        // bounded by MaxNestingDepth rather than overflowing the stack.
        var tableA = new LootTable("cycle_a", new List<LootDrop>
        {
            new FixedDrop("wood", chance: 1.0, minQty: 1, maxQty: 1),
            new TableRefDrop("cycle_b"),
        });
        var tableB = new LootTable("cycle_b", new List<LootDrop>
        {
            new FixedDrop("stone", chance: 1.0, minQty: 1, maxQty: 1),
            new TableRefDrop("cycle_a"),
        });
        var registry = new LootTableRegistry(new[] { tableA, tableB }, Items);

        var rng = new Random(5);
        var stacks = registry.Roll("cycle_a", rng);

        // It terminates (no stack overflow) and the count is bounded by the depth budget — never unbounded.
        Assert.NotEmpty(stacks);
        Assert.True(stacks.Count <= LootTableRegistry.MaxNestingDepth + 2,
            $"Cycle produced {stacks.Count} stacks — guard did not bound the recursion.");
    }

    // ---- Determinism -------------------------------------------------------------------------------------

    [Fact]
    public void SameSeedReproducesTheSameRoll()
    {
        var registry = DefaultRegistry();

        var a = new List<ItemStack>();
        var rngA = new Random(20260621);
        for (var i = 0; i < 100; i++)
        {
            a.AddRange(registry.Roll("slime_loot", rngA));
        }

        var b = new List<ItemStack>();
        var rngB = new Random(20260621);
        for (var i = 0; i < 100; i++)
        {
            b.AddRange(registry.Roll("slime_loot", rngB));
        }

        Assert.Equal(a, b); // ItemStack is a record struct => structural equality
    }

    // ---- Registry validation -----------------------------------------------------------------------------

    [Fact]
    public void ConstructorRejectsUnknownResourceReference()
    {
        var table = new LootTable("bad", new List<LootDrop>
        {
            new FixedDrop("not_a_real_resource", chance: 1.0, minQty: 1, maxQty: 1),
        });

        Assert.Throws<ArgumentException>(() => new LootTableRegistry(new[] { table }, Items));
    }

    [Fact]
    public void ConstructorRejectsUnknownTableReference()
    {
        var table = new LootTable("bad", new List<LootDrop>
        {
            new TableRefDrop("nope"),
        });

        Assert.Throws<ArgumentException>(() => new LootTableRegistry(new[] { table }, Items));
    }

    [Fact]
    public void ConstructorRejectsDuplicateTableId()
    {
        var a = new LootTable("dup", new List<LootDrop> { new FixedDrop("wood", 1.0, 1, 1) });
        var b = new LootTable("dup", new List<LootDrop> { new FixedDrop("stone", 1.0, 1, 1) });

        Assert.Throws<ArgumentException>(() => new LootTableRegistry(new[] { a, b }, Items));
    }
}
