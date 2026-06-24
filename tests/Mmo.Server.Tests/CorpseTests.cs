using System;
using System.Linq;
using Mmo.Server.Runtime;
using Mmo.Shared.Domain;
using Xunit;

namespace Mmo.Server.Tests;

// LOOT P4b — headless coverage of the Corpse loot payload (the server-side state behind a Corpse entity). Asserts:
// a corpse holds the rolled stacks + eligibility + decay deadline; an eligible loot-all transfers everything into the
// inventory and empties the corpse; a non-eligible looter is gated by IsEligible (the GameServer rejects, loot
// untouched); a full inventory leaves the remainder IN the corpse (nothing vanishes); decay is deadline-driven;
// duplicate stacks coalesce. Pure — uses a real Inventory over ItemRegistry.Default, no live server.
public sealed class CorpseTests
{
    private static Inventory NewInventory() => new(ItemRegistry.Default);

    private static Corpse SlimeCorpse(Guid eligible, params ItemStack[] contents) =>
        new(entityId: 1, contents, [eligible], LootMode.FfaAmongEligible, decayAtTick: 1000);

    [Fact]
    public void CorpseHoldsRolledStacksEligibilityAndDecay()
    {
        var looter = Guid.NewGuid();
        var corpse = new Corpse(
            entityId: 7,
            [new ItemStack("slime_gel", 2), new ItemStack("arcane_dust", 1)],
            [looter],
            LootMode.FfaAmongEligible,
            decayAtTick: 500);

        Assert.Equal(7ul, corpse.EntityId);
        Assert.Equal(LootMode.FfaAmongEligible, corpse.Mode);
        Assert.Equal(500u, corpse.DecayAtTick);
        Assert.True(corpse.IsEligible(looter));
        Assert.False(corpse.IsEligible(Guid.NewGuid()));
        Assert.False(corpse.IsEmpty);
        Assert.Equal(2, corpse.Contents.Count);
    }

    [Fact]
    public void EligibleLootAllTransfersEverythingAndEmptiesCorpse()
    {
        var looter = Guid.NewGuid();
        var corpse = SlimeCorpse(looter, new ItemStack("slime_gel", 3), new ItemStack("arcane_dust", 1));
        var inventory = NewInventory();

        var result = corpse.TryLootAll(inventory);

        Assert.True(result.Looted);
        Assert.True(result.CorpseEmptied);
        Assert.True(corpse.IsEmpty);
        Assert.Equal(3, inventory.QuantityOf("slime_gel"));
        Assert.Equal(1, inventory.QuantityOf("arcane_dust"));
        // Transferred reports what moved (for the toast).
        Assert.Contains(result.Transferred, s => s.TemplateKey == "slime_gel" && s.Quantity == 3);
        Assert.Contains(result.Transferred, s => s.TemplateKey == "arcane_dust" && s.Quantity == 1);
    }

    [Fact]
    public void NonEligibleLooterIsGatedAndLootUntouched()
    {
        // The eligibility gate is IsEligible (the GameServer rejects before calling TryLootAll). Assert the gate
        // itself + that NOT looting leaves the corpse full.
        var killer = Guid.NewGuid();
        var stranger = Guid.NewGuid();
        var corpse = SlimeCorpse(killer, new ItemStack("slime_gel", 2));

        Assert.False(corpse.IsEligible(stranger));
        // The corpse still holds everything (no transfer happened for the stranger).
        Assert.False(corpse.IsEmpty);
        Assert.Equal(2, corpse.Contents.Single(s => s.TemplateKey == "slime_gel").Quantity);
    }

    [Fact]
    public void FullInventoryLeavesRemainderInCorpse()
    {
        var looter = Guid.NewGuid();
        // slime_gel MaxStack is 99. Pre-fill the inventory to 98 so only 1 more fits; the corpse holds 5.
        var inventory = NewInventory();
        inventory.TryAdd("slime_gel", 98);

        var corpse = SlimeCorpse(looter, new ItemStack("slime_gel", 5));
        var result = corpse.TryLootAll(inventory);

        Assert.True(result.Looted); // 1 moved.
        Assert.False(result.CorpseEmptied);
        Assert.False(corpse.IsEmpty);
        Assert.Equal(99, inventory.QuantityOf("slime_gel"));
        // The 4 that didn't fit stay in the corpse — nothing vanished.
        Assert.Equal(4, corpse.Contents.Single(s => s.TemplateKey == "slime_gel").Quantity);
        Assert.Equal(1, result.Transferred.Single().Quantity);
    }

    [Fact]
    public void TotallyFullInventoryLootsNothingAndKeepsCorpse()
    {
        var looter = Guid.NewGuid();
        var inventory = NewInventory();
        inventory.TryAdd("slime_gel", 99); // capped.

        var corpse = SlimeCorpse(looter, new ItemStack("slime_gel", 3));
        var result = corpse.TryLootAll(inventory);

        Assert.False(result.Looted);
        Assert.False(result.CorpseEmptied);
        Assert.Equal(3, corpse.Contents.Single().Quantity);
    }

    [Fact]
    public void PartialFullStillLootsOtherStacks()
    {
        var looter = Guid.NewGuid();
        var inventory = NewInventory();
        inventory.TryAdd("slime_gel", 99); // gel capped; dust has room.

        var corpse = SlimeCorpse(looter, new ItemStack("slime_gel", 2), new ItemStack("arcane_dust", 1));
        var result = corpse.TryLootAll(inventory);

        Assert.True(result.Looted);
        Assert.False(result.CorpseEmptied); // gel remainder stays.
        Assert.Equal(1, inventory.QuantityOf("arcane_dust"));
        Assert.Equal(2, corpse.Contents.Single(s => s.TemplateKey == "slime_gel").Quantity);
        Assert.DoesNotContain(corpse.Contents, s => s.TemplateKey == "arcane_dust");
    }

    // LOOT P4c: TryTakeItem moves exactly the named stack and reports CorpseEmptied when it was the last one.
    [Fact]
    public void TakeItemTransfersTheNamedStackAndEmptiesWhenLast()
    {
        var looter = Guid.NewGuid();
        var corpse = SlimeCorpse(looter, new ItemStack("slime_gel", 3), new ItemStack("arcane_dust", 1));
        var inventory = NewInventory();

        var gel = corpse.TryTakeItem("slime_gel", inventory);
        Assert.True(gel.Took);
        Assert.Equal(3, gel.Transferred.Quantity);
        Assert.Equal("slime_gel", gel.Transferred.TemplateKey);
        Assert.False(gel.CorpseEmptied); // arcane_dust still in the corpse.
        Assert.Equal(3, inventory.QuantityOf("slime_gel"));
        Assert.DoesNotContain(corpse.Contents, s => s.TemplateKey == "slime_gel");

        var dust = corpse.TryTakeItem("arcane_dust", inventory);
        Assert.True(dust.Took);
        Assert.True(dust.CorpseEmptied); // last stack taken.
        Assert.True(corpse.IsEmpty);
        Assert.Equal(1, inventory.QuantityOf("arcane_dust"));
    }

    // LOOT P4c: taking an item not in the corpse (or an empty key) is a no-op — nothing moves, corpse untouched.
    [Fact]
    public void TakeItemUnknownKeyIsNoOp()
    {
        var corpse = SlimeCorpse(Guid.NewGuid(), new ItemStack("slime_gel", 2));
        var inventory = NewInventory();

        var result = corpse.TryTakeItem("crystal_shard", inventory);

        Assert.False(result.Took);
        Assert.False(result.CorpseEmptied);
        Assert.Equal(0, inventory.QuantityOf("crystal_shard"));
        Assert.Equal(2, corpse.Contents.Single().Quantity);
    }

    // LOOT P4c: a full inventory leaves the taken stack's remainder IN the corpse (nothing vanishes), like loot-all.
    [Fact]
    public void TakeItemFullInventoryLeavesRemainder()
    {
        var inventory = NewInventory();
        inventory.TryAdd("slime_gel", 98); // room for 1 more (MaxStack 99).

        var corpse = SlimeCorpse(Guid.NewGuid(), new ItemStack("slime_gel", 5));
        var result = corpse.TryTakeItem("slime_gel", inventory);

        Assert.True(result.Took);
        Assert.Equal(1, result.Transferred.Quantity);
        Assert.False(result.CorpseEmptied);
        Assert.Equal(99, inventory.QuantityOf("slime_gel"));
        Assert.Equal(4, corpse.Contents.Single().Quantity); // the 4 that didn't fit stay.
    }

    // LOOT P4c: a totally full inventory takes nothing — Took=false, corpse keeps the stack.
    [Fact]
    public void TakeItemTotallyFullIsNoOp()
    {
        var inventory = NewInventory();
        inventory.TryAdd("slime_gel", 99); // capped.

        var corpse = SlimeCorpse(Guid.NewGuid(), new ItemStack("slime_gel", 3));
        var result = corpse.TryTakeItem("slime_gel", inventory);

        Assert.False(result.Took);
        Assert.False(result.CorpseEmptied);
        Assert.Equal(3, corpse.Contents.Single().Quantity);
    }

    [Fact]
    public void DecayIsDeadlineDriven()
    {
        var corpse = SlimeCorpse(Guid.NewGuid(), new ItemStack("slime_gel", 1));
        Assert.False(corpse.IsDecayed(999));
        Assert.True(corpse.IsDecayed(1000));
        Assert.True(corpse.IsDecayed(1001));
    }

    [Fact]
    public void DuplicateStacksCoalesce()
    {
        // A loot roll can emit the same resource in two bands (floor + a pool hit). The corpse merges them.
        var corpse = SlimeCorpse(
            Guid.NewGuid(),
            new ItemStack("slime_gel", 2),
            new ItemStack("slime_gel", 3));

        Assert.Single(corpse.Contents);
        Assert.Equal(5, corpse.Contents.Single().Quantity);
    }
}
