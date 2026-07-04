using System.Collections.Generic;
using Mmo.Server.Runtime;
using Mmo.Shared.Domain;
using Xunit;

namespace Mmo.Server.Tests;

// DUO-WAVE2 ability 2 (Unison Shield): the shield absorbs INSIDE the REAL PlayerDamageGate — the single player-damage
// choke point — between the i-frame check and ApplyDamage. Driven against the genuine gate (its i-frame + dead guards
// intact) with the SAME absorb closure GameServer wires (victim.OwnerSession.AbsorbWithShield), so deleting the
// absorb step or mis-ordering it fails these. Pins: a wholly-absorbed hit is negated (health unchanged, no landed
// tail); a partial hit applies the remainder; the pool decrements per hit and empties; an expired shield absorbs
// nothing; the solo/shared strengths behave (weaker never overwrites stronger).
public sealed class ShieldAbsorptionTests
{
    private static (PlayerDamageGate Gate, ClientSession Session, WorldEntity Player, List<int> Landed) CreateHarness()
    {
        var session = new ClientSession(null!);
        var player = new WorldEntity(
            id: 1, networkId: 1, EntityKind.Player, new TileCoord(10, 10), Direction8.S,
            displayName: "Hero", characterId: null, ownerSession: session, isDurable: false);
        var landed = new List<int>();
        var gate = new PlayerDamageGate(
            hasActiveIFrames: (_, _) => false,
            onDamageLanded: (_, amount, _) => landed.Add(amount),
            tryAbsorbShield: (victim, amount, tick) => victim.OwnerSession is { } s ? s.AbsorbWithShield(amount, tick) : 0);
        return (gate, session, player, landed);
    }

    [Fact]
    public void WhollyAbsorbedHit_IsNegated_HealthUnchanged_NoLandedTail()
    {
        var (gate, session, player, landed) = CreateHarness();
        session.ArmShield(strength: 25, expiryTick: 1000, serverTick: 0);

        var landedHit = gate.TryDamagePlayer(player, 10, serverTick: 5, "test");

        Assert.False(landedHit);              // fully absorbed — did not land
        Assert.Equal(100, player.Stats.Health);
        Assert.Empty(landed);                 // the landed tail never ran
        Assert.Equal(15, session.ShieldRemainingAt(5)); // pool decremented 25 -> 15
    }

    [Fact]
    public void PartialHit_AppliesRemainder_AndEmptiesPool()
    {
        var (gate, session, player, landed) = CreateHarness();
        session.ArmShield(strength: 25, expiryTick: 1000, serverTick: 0);

        Assert.False(gate.TryDamagePlayer(player, 10, 5, "a")); // 25 -> 15
        Assert.False(gate.TryDamagePlayer(player, 10, 6, "b")); // 15 -> 5
        var third = gate.TryDamagePlayer(player, 10, 7, "c");   // absorb 5, 5 remainder LANDS

        Assert.True(third);
        Assert.Equal(95, player.Stats.Health);         // only the 5-remainder landed
        Assert.Single(landed);
        Assert.Equal(5, landed[0]);
        Assert.Equal(0, session.ShieldRemainingAt(7));  // pool empty
    }

    [Fact]
    public void ExpiredShield_AbsorbsNothing_FullDamageLands()
    {
        var (gate, session, player, landed) = CreateHarness();
        session.ArmShield(strength: 40, expiryTick: 80, serverTick: 0);

        // A hit AT/after the expiry tick is not absorbed.
        var landedHit = gate.TryDamagePlayer(player, 30, serverTick: 80, "late");

        Assert.True(landedHit);
        Assert.Equal(70, player.Stats.Health);
        Assert.Equal(30, Assert.Single(landed));
    }

    [Fact]
    public void ArmShield_KeepsTheStronger_SoloDoesNotOverwriteShared()
    {
        var (_, session, _, _) = CreateHarness();
        session.ArmShield(strength: 40, expiryTick: 1000, serverTick: 0); // shared/Perfect pool
        session.ArmShield(strength: 10, expiryTick: 1000, serverTick: 1); // a weaker solo arm must not weaken it

        Assert.Equal(40, session.ShieldRemainingAt(1));
    }
}
