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
        var entity = CreateDummy(); // spawns at DefaultSpawnTile (8,8).
        entity.SetSpeedUnitsPerSecond(5d);

        // Arm the ATTACK cooldown at tick 0.
        Assert.True(entity.TryBeginAttack(0, 100));

        // Movement at tick 0 must still be allowed — the attack cooldown is a SEPARATE clock and does NOT freeze
        // the movement integrator (IsMovementFrozen reads only the movement gate, untouched by TryBeginAttack).
        Assert.False(entity.IsMovementFrozen(0));
        Assert.True(IntegrateIfNotFrozen(entity, Direction8.E, serverTick: 0)); // moved

        // And the attack cooldown is unaffected by the move: still on cooldown at tick 1.
        Assert.False(entity.TryBeginAttack(1, 100));
    }

    [Fact]
    public void AttackMovementRootDelaysNextMoveByRootTicksThenAllowsIt()
    {
        // SWING-COMMIT: an accepted swing roots MOVEMENT — the player is frozen (IsMovementFrozen) for rootTicks,
        // so the integrator caller withholds the move, then it resumes. Start fresh, root at tick 0 for 4 ticks,
        // hold E. The invariant: a swing-rooted player does NOT move inside the window and DOES at the boundary.
        var entity = CreateDummy();
        entity.SetSpeedUnitsPerSecond(5d);
        const uint rootTicks = 4;

        entity.ApplyAttackMovementRoot(0, rootTicks);
        var startPosition = entity.Position;

        // Inside the root window [0, rootTicks): frozen, so the gated integrator withholds the move — position
        // does not change.
        for (uint tick = 0; tick < rootTicks; tick++)
        {
            Assert.True(entity.IsMovementFrozen(tick));
            Assert.False(IntegrateIfNotFrozen(entity, Direction8.E, tick)); // suppressed
            Assert.Equal(startPosition, entity.Position);
        }

        // At rootTicks the freeze has elapsed: the integrator runs and the entity advances.
        Assert.False(entity.IsMovementFrozen(rootTicks));
        Assert.True(IntegrateIfNotFrozen(entity, Direction8.E, rootTicks)); // moved
        Assert.True(entity.Position.X > startPosition.X);
    }

    [Fact]
    public void AttackMovementRootIsAFloorNeverShortensALongerExistingCooldown()
    {
        // The root is max(existing, serverTick + rootTicks) — it must never pull an already-LATER movement freeze
        // earlier. Apply a LONG root at tick 10 (freeze until tick 30), then a SHORT root anchored at tick 12 (its
        // window 12 + 4 = 16 is earlier than 30): the floor must leave the freeze at 30, unchanged.
        var entity = CreateDummy();

        entity.ApplyAttackMovementRoot(10, rootTicks: 20); // frozen until 30
        entity.ApplyAttackMovementRoot(12, rootTicks: 4);  // 16 < 30 -> floor leaves it at 30

        // Still frozen at 29 (the longer root wins), free at 30.
        Assert.True(entity.IsMovementFrozen(29));
        Assert.False(entity.IsMovementFrozen(30));
    }

    [Fact]
    public void AttackMovementRootDoesNotAffectAttackCadence()
    {
        // The root is a MOVEMENT gate only — it must not touch the INDEPENDENT attack cooldown. Arm the attack
        // cooldown at tick 0, then apply a movement root: the attack cooldown is unchanged (still rejecting), and
        // a separate attack-eligibility check is governed solely by the attack cooldown, not the root.
        var entity = CreateDummy();
        const uint attackCooldown = 12;

        Assert.True(entity.TryBeginAttack(0, attackCooldown));   // arms attack cooldown -> eligible again at 12
        entity.ApplyAttackMovementRoot(0, rootTicks: 4);          // movement root only

        // Attack cadence is governed purely by the attack cooldown: still rejected before 12, accepted at 12 —
        // the movement root neither shortened nor lengthened it.
        Assert.False(entity.TryBeginAttack(11, attackCooldown));
        Assert.True(entity.TryBeginAttack(12, attackCooldown));
    }

    [Fact]
    public void AuthoredAttackRootAnchorsOnAuthoredTickNotReceiveTick()
    {
        // SWING-COMMIT-FIX: the server roots on the message's AUTHORED tick, not its receive tick. The client authored
        // the swing at tick 6 and the server received it at tick 8 (latency 2). The root window must end at
        // authored(6) + rootTicks(4) = 10 — the SAME tick the predictor (which rooted at the authored tick) resumes —
        // NOT at receive(8) + 4 = 12. So the step is rejected up to 9 and accepted at 10.
        var entity = CreateDummy();
        const uint rootTicks = 4;
        const uint authoredTick = 6;
        const uint receiveTick = 8;     // latency 2

        entity.ApplyAttackMovementRootAuthored(authoredTick, receiveTick, rootTicks, pastWindowTicks: 64, futureLeadTicks: 4);

        // Frozen at tick 9 (inside the authored window [6,10)); free at tick 10 (authored 6 + rootTicks 4).
        Assert.True(entity.IsMovementFrozen(9));
        Assert.False(entity.IsMovementFrozen(10));
    }

    [Fact]
    public void AuthoredAttackRootClampsFarFutureAuthoredTickToWindowCeil()
    {
        // SWING-COMMIT-FIX anti-cheat: a hostile/buggy client that stamps a far-FUTURE authored tick cannot push the
        // root window arbitrarily far out. The authored tick is clamped to receiveTick + futureLead before use, so the
        // root ends at (receiveTick + futureLead) + rootTicks, not (absurdFutureTick) + rootTicks.
        var entity = CreateDummy();
        const uint rootTicks = 4;
        const uint receiveTick = 10;
        const uint futureLead = 4;
        const uint absurdFutureAuthored = 10_000;   // way past the window ceiling (14)

        entity.ApplyAttackMovementRootAuthored(absurdFutureAuthored, receiveTick, rootTicks, pastWindowTicks: 64, futureLeadTicks: futureLead);

        // Clamped authored = receiveTick(10) + futureLead(4) = 14; root window ends at 14 + 4 = 18 — NOT 10_004.
        Assert.True(entity.IsMovementFrozen(17));
        Assert.False(entity.IsMovementFrozen(18));
    }

    [Fact]
    public void AuthoredAttackRootClampsFarPastAuthoredTickToWindowFloor()
    {
        // SWING-COMMIT-FIX anti-cheat: a far-PAST authored tick (a very stale/tampered stamp) cannot dodge the
        // committed-swing penalty by making the root a no-op. The authored tick is clamped UP to
        // receiveTick - pastWindow, so the root still withholds movement for a window anchored there.
        var entity = CreateDummy();
        const uint rootTicks = 4;
        const uint receiveTick = 100;
        const uint pastWindow = 64;

        // Authored tick 2 is far below the floor (100 - 64 = 36); it is clamped up to 36, so the window ends at 40.
        entity.ApplyAttackMovementRootAuthored(2, receiveTick, rootTicks, pastWindowTicks: pastWindow, futureLeadTicks: 4);

        Assert.True(entity.IsMovementFrozen(39));
        Assert.False(entity.IsMovementFrozen(40));
    }

    [Fact]
    public void AttackMovementRootTickCountMatchesCombatTuning()
    {
        // The server derives rootTicks from CombatTuning.RootTicks(tickRate, rootMs) and the predictor from
        // RootTicksFromTickMs(tickMs, rootMs). At 20 Hz they must agree — the parity invariant the live root depends
        // on. Tested at an explicit rootMs (the live DEFAULT is now 0 = no root). (Pinned in the server suite too.)
        const int tickRate = 20;
        const int rootMs = 200;
        var fromRate = CombatTuning.RootTicks(tickRate, rootMs);
        var fromTickMs = CombatTuning.RootTicksFromTickMs(1000d / tickRate, rootMs);
        Assert.True(fromRate >= 1);
        Assert.Equal(fromRate, fromTickMs);
        // 200 ms at 50 ms/tick = 4 ticks (Ceiling).
        Assert.Equal(4u, fromRate);
    }

    [Fact]
    public void RegenHealthAddsTowardMaxAndReportsChange()
    {
        // COMBAT-QOL: TryRegenHealth ADDS toward MaxHealth (the inverse of ApplyDamage). Start damaged, heal up.
        var entity = CreateDummy();
        Assert.True(entity.TrySetStatCurrent(StatKind.Health, 40));

        Assert.True(entity.TryRegenHealth(25));
        Assert.Equal(65, entity.Stats.Health);

        Assert.True(entity.TryRegenHealth(25));
        Assert.Equal(90, entity.Stats.Health);
    }

    [Fact]
    public void RegenHealthClampsAtMaxAndDoesNotOvershoot()
    {
        // A heavy regen that would exceed MaxHealth is clamped to max — never overshoots.
        var entity = CreateDummy();
        Assert.True(entity.TrySetStatCurrent(StatKind.Health, 90));

        // 25 against 90/100 lands on 100 (clamped), and reports a change.
        Assert.True(entity.TryRegenHealth(25));
        Assert.Equal(100, entity.Stats.Health);
    }

    [Fact]
    public void RegenHealthIsNoOpAtFull()
    {
        // At full HP regen reports NO change (and must not bump StateRevision) — a healthy dummy costs nothing.
        var entity = CreateDummy();
        Assert.Equal(100, entity.Stats.Health);
        var before = entity.StateRevision;

        Assert.False(entity.TryRegenHealth(50));
        Assert.Equal(100, entity.Stats.Health);
        Assert.Equal(before, entity.StateRevision);
    }

    [Fact]
    public void RegenHealthIgnoresNonPositiveAmount()
    {
        var entity = CreateDummy();
        Assert.True(entity.TrySetStatCurrent(StatKind.Health, 50));
        var before = entity.StateRevision;

        Assert.False(entity.TryRegenHealth(0));
        Assert.False(entity.TryRegenHealth(-30));
        Assert.Equal(50, entity.Stats.Health);
        Assert.Equal(before, entity.StateRevision);
    }

    [Fact]
    public void RegenHealthBumpsStateRevisionOnRealChange()
    {
        // A real heal bumps StateRevision so the refilled HP re-replicates through the snapshot delta path.
        var entity = CreateDummy();
        Assert.True(entity.TrySetStatCurrent(StatKind.Health, 50));
        var before = entity.StateRevision;

        Assert.True(entity.TryRegenHealth(10));
        Assert.True(entity.StateRevision > before);
    }

    // Mirrors GameServer.HandleMoveIntent's freeze gate: a frozen player's input is withheld (no integrate); an
    // unfrozen player integrates one tick. Returns true iff the entity actually moved. This is the exact path the
    // attack-movement-root protects — the root pushes _nextEligibleTick forward, IsMovementFrozen reports it, and
    // the caller skips the move while frozen.
    private static bool IntegrateIfNotFrozen(WorldEntity entity, Direction8 direction, uint serverTick)
    {
        if (entity.IsMovementFrozen(serverTick))
        {
            return false;
        }

        entity.IntegrateMovement(direction.ToUnitVector(), dtSeconds: 0.05d);
        return true;
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
