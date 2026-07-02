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
    public void SlimeCoreDropsAtApproximatelyItsRate()
    {
        // loot-followups #2: the rarest drop (slime_core, chance 0.0008) gets its own rate pin, mirroring the
        // rare-tail test. Over 200k seeded rolls expected ~160 hits; the wide ±0.0004 band (>6σ at this N)
        // catches gross errors (order-of-magnitude, inversion) without flaking on the fixed seed.
        var registry = DefaultRegistry();
        var rng = new Random(2026);
        const int rolls = 200_000;
        var cores = 0;

        for (var i = 0; i < rolls; i++)
        {
            foreach (var stack in registry.Roll("slime_loot", rng))
            {
                if (stack.TemplateKey == "slime_core")
                {
                    cores++;
                }
            }
        }

        var rate = cores / (double)rolls;
        Assert.InRange(rate, 0.0008 - 0.0004, 0.0008 + 0.0004);
    }

    [Fact]
    public void EachDropResolvesIndependently()
    {
        // loot-followups #2: the "each drop resolves independently" claim, asserted DIRECTLY on a synthetic
        // table of two 50% drops: if the drops are independent, the joint hit rate is p1·p2 = 0.25 (and each
        // marginal is 0.5). Correlated resolution (a shared roll / one drop gating the other) lands far
        // outside the band.
        var table = new LootTable("indep", new List<LootDrop>
        {
            new FixedDrop("wood", chance: 0.5, minQty: 1, maxQty: 1),
            new FixedDrop("stone", chance: 0.5, minQty: 1, maxQty: 1),
        });
        var registry = new LootTableRegistry(new[] { table }, Items);
        var rng = new Random(424242);
        const int rolls = 100_000;
        int wood = 0, stone = 0, both = 0;

        for (var i = 0; i < rolls; i++)
        {
            var stacks = registry.Roll("indep", rng);
            var hasWood = stacks.Any(s => s.TemplateKey == "wood");
            var hasStone = stacks.Any(s => s.TemplateKey == "stone");
            if (hasWood) wood++;
            if (hasStone) stone++;
            if (hasWood && hasStone) both++;
        }

        Assert.InRange(wood / (double)rolls, 0.47, 0.53);
        Assert.InRange(stone / (double)rolls, 0.47, 0.53);
        Assert.InRange(both / (double)rolls, 0.23, 0.27);
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
    public void DepthGuardBoundsRunawayAcyclicNesting()
    {
        // Cycles are now rejected at CONSTRUCTION (see the validation tests below), so the roll-time depth
        // guard's remaining job is bounding a legitimately-constructible but pathologically DEEP acyclic
        // chain. Build chain_0 -> chain_1 -> ... -> chain_10 (each dropping one wood): construction passes
        // (acyclic), and Roll terminates at the depth budget — exactly one wood per table for depths
        // 0..MaxNestingDepth, the deeper refs dropped (logged), never a stack overflow.
        var tables = new List<LootTable>();
        for (var i = 0; i <= 10; i++)
        {
            var drops = new List<LootDrop> { new FixedDrop("wood", chance: 1.0, minQty: 1, maxQty: 1) };
            if (i < 10)
            {
                drops.Add(new TableRefDrop($"chain_{i + 1}"));
            }

            tables.Add(new LootTable($"chain_{i}", drops));
        }

        var registry = new LootTableRegistry(tables, Items);

        var stacks = registry.Roll("chain_0", new Random(5));

        Assert.Equal(LootTableRegistry.MaxNestingDepth + 1, stacks.Count);
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

    [Fact]
    public void ConstructorRejectsNestingCycle()
    {
        // loot-followups #1: an authoring cycle (a -> b -> a, here via BOTH ref kinds — a TableRefDrop and a
        // tableRef weightedPick option) must fail fast at REGISTRY CONSTRUCTION, naming the cycle — not be
        // discovered one warn-log at a time at roll time under the depth guard.
        var tableA = new LootTable("cycle_a", new List<LootDrop>
        {
            new FixedDrop("wood", chance: 1.0, minQty: 1, maxQty: 1),
            new TableRefDrop("cycle_b"),
        });
        var tableB = new LootTable("cycle_b", new List<LootDrop>
        {
            new WeightedPickDrop([WeightedPickOption.TableRef("cycle_a", weight: 1)], emptyWeight: 0),
        });

        var ex = Assert.Throws<ArgumentException>(() => new LootTableRegistry(new[] { tableA, tableB }, Items));
        Assert.Contains("cycle", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cycle_a", ex.Message);
        Assert.Contains("cycle_b", ex.Message);
    }

    [Fact]
    public void ConstructorRejectsSelfReference()
    {
        var table = new LootTable("selfie", new List<LootDrop> { new TableRefDrop("selfie") });

        Assert.Throws<ArgumentException>(() => new LootTableRegistry(new[] { table }, Items));
    }
}
