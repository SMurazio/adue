using System;
using Mmo.Server.Runtime;
using Xunit;

namespace Mmo.Server.Tests;

// LOOT P4b — headless coverage of the contribution ledger (the group-loot eligibility groundwork). Asserts: a
// damaging player becomes a contributor; multiple damagers all become contributors (the multi-damager eligible
// set); cumulative damage is tracked; an empty contributor id is ignored; a monster never damaged has an empty
// set; Forget removes an entry (no leak). Pure — no world/session/protocol needed.
public sealed class ContributionLedgerTests
{
    [Fact]
    public void RecordedDamagerBecomesContributor()
    {
        var ledger = new ContributionLedger();
        var player = Guid.NewGuid();

        ledger.RecordDamage(monsterId: 1, player, damage: 20);

        Assert.Contains(player, ledger.Contributors(1));
        Assert.Equal(20, ledger.DamageBy(1, player));
    }

    [Fact]
    public void MultipleDamagersAllBecomeEligible()
    {
        var ledger = new ContributionLedger();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        ledger.RecordDamage(1, a, 10);
        ledger.RecordDamage(1, b, 30);
        ledger.RecordDamage(1, a, 5); // a hits again — still one contributor entry, accumulated damage.

        var contributors = ledger.Contributors(1);
        Assert.Equal(2, contributors.Count);
        Assert.Contains(a, contributors);
        Assert.Contains(b, contributors);
        Assert.Equal(15, ledger.DamageBy(1, a));
        Assert.Equal(30, ledger.DamageBy(1, b));
    }

    [Fact]
    public void ZeroDamageStillRegistersContributorButAddsNoDamage()
    {
        // A connecting swing on an already-low target can report 0 amount; the swinger still earned eligibility.
        var ledger = new ContributionLedger();
        var player = Guid.NewGuid();

        ledger.RecordDamage(1, player, damage: 0);

        Assert.Contains(player, ledger.Contributors(1));
        Assert.Equal(0, ledger.DamageBy(1, player));
    }

    [Fact]
    public void EmptyContributorIdIsIgnored()
    {
        var ledger = new ContributionLedger();

        ledger.RecordDamage(1, Guid.Empty, 50);

        Assert.Empty(ledger.Contributors(1));
        Assert.Equal(0, ledger.TrackedMonsterCount);
    }

    [Fact]
    public void NeverDamagedMonsterHasEmptyEligibleSet()
    {
        var ledger = new ContributionLedger();
        Assert.Empty(ledger.Contributors(999));
    }

    [Fact]
    public void ForgetRemovesTheEntryNoLeak()
    {
        var ledger = new ContributionLedger();
        ledger.RecordDamage(1, Guid.NewGuid(), 10);
        ledger.RecordDamage(2, Guid.NewGuid(), 10);
        Assert.Equal(2, ledger.TrackedMonsterCount);

        ledger.Forget(1);
        Assert.Equal(1, ledger.TrackedMonsterCount);
        Assert.Empty(ledger.Contributors(1));

        ledger.Forget(2);
        Assert.Equal(0, ledger.TrackedMonsterCount);

        // Idempotent: forgetting an unknown id is a no-op.
        ledger.Forget(12345);
        Assert.Equal(0, ledger.TrackedMonsterCount);
    }
}
