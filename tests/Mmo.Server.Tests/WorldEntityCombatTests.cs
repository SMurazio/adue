using Mmo.Server.Runtime;
using Mmo.Shared.Domain;
using Xunit;

namespace Mmo.Server.Tests;

// COMBAT-S2B: WorldEntity's combat primitives — ApplyDamage (subtract + clamp [0, max], change-report) and the
// INDEPENDENT per-entity attack cooldown gate (TryBeginAttack), modelled separately from the move cooldown.
public sealed class WorldEntityCombatTests
{
    [Fact]
    public void ApplyDamageSubtractsAndReportsChange()
    {
        var entity = CreateDummy();
        Assert.Equal(100, entity.Stats.Health);

        Assert.True(entity.ApplyDamage(20));
        Assert.Equal(80, entity.Stats.Health);

        Assert.True(entity.ApplyDamage(20));
        Assert.Equal(60, entity.Stats.Health);
    }

    [Fact]
    public void ApplyDamageClampsAtZeroAndDoesNotGoNegative()
    {
        var entity = CreateDummy();
        Assert.True(entity.TrySetStatCurrent(StatKind.Health, 15));

        // 20 damage against 15 HP lands on 0 (clamped), and reports a change.
        Assert.True(entity.ApplyDamage(20));
        Assert.Equal(0, entity.Stats.Health);

        // Already at 0 — further damage reports NO change (HP cannot go negative; no death/despawn this stage).
        Assert.False(entity.ApplyDamage(20));
        Assert.Equal(0, entity.Stats.Health);
    }

    [Fact]
    public void ApplyDamageIgnoresNonPositiveAmount()
    {
        var entity = CreateDummy();

        Assert.False(entity.ApplyDamage(0));
        Assert.False(entity.ApplyDamage(-50));
        Assert.Equal(100, entity.Stats.Health);
    }

    [Fact]
    public void ApplyDamageBumpsStateRevisionOnRealHit()
    {
        var entity = CreateDummy();
        var before = entity.StateRevision;

        Assert.True(entity.ApplyDamage(20));
        Assert.True(entity.StateRevision > before);

        // A no-op damage (already 0) must NOT bump the revision.
        Assert.True(entity.TrySetStatCurrent(StatKind.Health, 0));
        var atZero = entity.StateRevision;
        Assert.False(entity.ApplyDamage(20));
        Assert.Equal(atZero, entity.StateRevision);
    }

    [Fact]
    public void AttackCooldownGatesRepeatAttacks()
    {
        var entity = CreateDummy();
        const uint cooldown = 12; // ticks

        // First attack at tick 100 is always eligible and arms the cooldown.
        Assert.True(entity.TryBeginAttack(100, cooldown));

        // Inside the cooldown window: rejected.
        Assert.False(entity.TryBeginAttack(101, cooldown));
        Assert.False(entity.TryBeginAttack(111, cooldown)); // one tick before eligible (100 + 12 = 112)

        // At the eligible tick: accepted again, and re-arms.
        Assert.True(entity.TryBeginAttack(112, cooldown));
        Assert.False(entity.TryBeginAttack(113, cooldown));
    }

    [Fact]
    public void AttackCooldownIsIndependentOfMoveCooldown()
    {
        var entity = CreateDummy(); // spawns at DefaultSpawnTile (8,8), well inside a 16x16 empty grid.
        var grid = new TileGrid(16, 16, []);

        // Arm the ATTACK cooldown at tick 0.
        Assert.True(entity.TryBeginAttack(0, 100));

        // A movement step at tick 0 must still be allowed (its own _nextEligibleTick clock is separate) — the
        // attack cooldown does NOT gate movement.
        Assert.True(entity.TryStep(Direction8.E, 0, stepCooldownTicks: 3, grid, out _));

        // And the attack cooldown is unaffected by the move: still on cooldown at tick 1.
        Assert.False(entity.TryBeginAttack(1, 100));
    }

    private static WorldEntity CreateDummy()
    {
        return new WorldEntity(
            id: 1,
            networkId: 1,
            EntityKind.Dummy,
            TileGrid.DefaultSpawnTile,
            Direction8.S,
            "Dummy",
            characterId: null,
            ownerSession: null,
            isDurable: false);
    }
}
