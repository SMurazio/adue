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

    [Fact]
    public void AttackMovementRootDelaysNextStepByRootTicksThenAllowsIt()
    {
        // SWING-COMMIT: an accepted swing roots MOVEMENT — the next held step is withheld for rootTicks, then
        // accepted. Start fresh (no prior step), root at tick 0 for 4 ticks, hold E into open space.
        var entity = CreateDummy();
        var grid = new TileGrid(16, 16, []);
        const uint rootTicks = 4;

        entity.ApplyAttackMovementRoot(0, rootTicks);
        var startTile = entity.Tile;

        // Inside the root window [0, rootTicks): every step is rejected and the tile does not move.
        for (uint tick = 0; tick < rootTicks; tick++)
        {
            Assert.False(entity.TryStep(Direction8.E, tick, stepCooldownTicks: 3, grid, out _));
            Assert.Equal(startTile, entity.Tile);
        }

        // At rootTicks the step is accepted (the root window has elapsed).
        Assert.True(entity.TryStep(Direction8.E, rootTicks, stepCooldownTicks: 3, grid, out _));
        Assert.Equal(startTile.Offset(1, 0), entity.Tile);
    }

    [Fact]
    public void AttackMovementRootIsAFloorNeverShortensALongerExistingCooldown()
    {
        // The root is max(existing, serverTick + rootTicks) — it must never pull an already-LATER movement cooldown
        // earlier. Step at tick 10 with a long cooldown (so _nextEligibleTick = 30), then root with a SHORT window
        // anchored at tick 12: the root window (12 + 4 = 16) is earlier than 30, so it must change nothing.
        var entity = CreateDummy();
        var grid = new TileGrid(16, 16, []);

        Assert.True(entity.TryStep(Direction8.E, 10, stepCooldownTicks: 20, grid, out _)); // next eligible = 30
        entity.ApplyAttackMovementRoot(12, rootTicks: 4); // 16 < 30 -> floor leaves it at 30

        // Still rejected at 29 (the longer step cooldown wins), accepted at 30.
        Assert.False(entity.TryStep(Direction8.E, 29, stepCooldownTicks: 20, grid, out _));
        Assert.True(entity.TryStep(Direction8.E, 30, stepCooldownTicks: 20, grid, out _));
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
        var grid = new TileGrid(16, 16, []);
        const uint rootTicks = 4;
        const uint authoredTick = 6;
        const uint receiveTick = 8;     // latency 2

        entity.ApplyAttackMovementRootAuthored(authoredTick, receiveTick, rootTicks, pastWindowTicks: 64, futureLeadTicks: 4);
        var startTile = entity.Tile;

        // Rejected at tick 9 (inside the authored window [6,10)); accepted at tick 10 (authored 6 + rootTicks 4).
        Assert.False(entity.TryStep(Direction8.S, 9, stepCooldownTicks: 3, grid, out _));
        Assert.Equal(startTile, entity.Tile);
        Assert.True(entity.TryStep(Direction8.S, 10, stepCooldownTicks: 3, grid, out _));
    }

    [Fact]
    public void AuthoredAttackRootClampsFarFutureAuthoredTickToWindowCeil()
    {
        // SWING-COMMIT-FIX anti-cheat: a hostile/buggy client that stamps a far-FUTURE authored tick cannot push the
        // root window arbitrarily far out. The authored tick is clamped to receiveTick + futureLead before use, so the
        // root ends at (receiveTick + futureLead) + rootTicks, not (absurdFutureTick) + rootTicks.
        var entity = CreateDummy();
        var grid = new TileGrid(16, 16, []);
        const uint rootTicks = 4;
        const uint receiveTick = 10;
        const uint futureLead = 4;
        const uint absurdFutureAuthored = 10_000;   // way past the window ceiling (14)

        entity.ApplyAttackMovementRootAuthored(absurdFutureAuthored, receiveTick, rootTicks, pastWindowTicks: 64, futureLeadTicks: futureLead);

        // Clamped authored = receiveTick(10) + futureLead(4) = 14; root window ends at 14 + 4 = 18 — NOT 10_004.
        Assert.False(entity.TryStep(Direction8.S, 17, stepCooldownTicks: 3, grid, out _));
        Assert.True(entity.TryStep(Direction8.S, 18, stepCooldownTicks: 3, grid, out _));
    }

    [Fact]
    public void AuthoredAttackRootClampsFarPastAuthoredTickToWindowFloor()
    {
        // SWING-COMMIT-FIX anti-cheat: a far-PAST authored tick (a very stale/tampered stamp) cannot dodge the
        // committed-swing penalty by making the root a no-op. The authored tick is clamped UP to
        // receiveTick - pastWindow, so the root still withholds movement for a window anchored there.
        var entity = CreateDummy();
        var grid = new TileGrid(16, 16, []);
        const uint rootTicks = 4;
        const uint receiveTick = 100;
        const uint pastWindow = 64;

        // Authored tick 2 is far below the floor (100 - 64 = 36); it is clamped up to 36, so the window ends at 40.
        entity.ApplyAttackMovementRootAuthored(2, receiveTick, rootTicks, pastWindowTicks: pastWindow, futureLeadTicks: 4);

        Assert.False(entity.TryStep(Direction8.S, 39, stepCooldownTicks: 3, grid, out _));
        Assert.True(entity.TryStep(Direction8.S, 40, stepCooldownTicks: 3, grid, out _));
    }

    [Fact]
    public void AttackMovementRootTickCountMatchesCombatTuning()
    {
        // The server derives rootTicks from CombatTuning.RootTicks(tickRate) and the predictor from
        // RootTicksFromTickMs(tickMs). At 20 Hz they must agree and be >= 1 — the parity invariant the live root
        // depends on. (Pinned here too so a server-side break is caught in the server suite.)
        const int tickRate = 20;
        var fromRate = CombatTuning.RootTicks(tickRate);
        var fromTickMs = CombatTuning.RootTicksFromTickMs(1000d / tickRate);
        Assert.True(fromRate >= 1);
        Assert.Equal(fromRate, fromTickMs);
        // 200 ms at 50 ms/tick = 4 ticks (Ceiling).
        Assert.Equal(4u, fromRate);
    }

    [Fact]
    public void SwingSlowFactorZeroBlocksStepsInsideWindow_LikeTheOldRoot()
    {
        // SWING-SLOW: factor 0 is the FULL-STOP case — a step inside the swing window is BLOCKED, reproducing the
        // old hard root exactly. Open a slow window [0, 4) at factor 0; every step inside is rejected, the first
        // step at/after the window end is accepted.
        var entity = CreateDummy();
        var grid = new TileGrid(16, 16, []);
        const uint slowDuration = 4;

        entity.ApplyAttackMovementSlowAuthored(0, 0, slowDuration, factor: 0d, pastWindowTicks: 64, futureLeadTicks: 4);
        var startTile = entity.Tile;

        for (uint tick = 0; tick < slowDuration; tick++)
        {
            Assert.False(entity.TryStep(Direction8.E, tick, stepCooldownTicks: 3, grid, out _));
            Assert.Equal(startTile, entity.Tile);
        }

        Assert.True(entity.TryStep(Direction8.E, slowDuration, stepCooldownTicks: 3, grid, out _));
        Assert.Equal(startTile.Offset(1, 0), entity.Tile);
    }

    [Fact]
    public void SwingSlowFactorInRangeSlowsButDoesNotFreezeMovement()
    {
        // SWING-SLOW: a non-zero factor SLOWS movement during the window rather than freezing it. With base cadence
        // 3 ticks and factor 0.4, a step ACCEPTED inside the window costs ceil(3 / 0.4) = 8 ticks before the next.
        // Hold E into open space, open a long slow window so the first post-swing step lands inside it, and assert
        // the gap to the following step is the slowed 8 ticks (movement continued — it did not stop).
        var entity = CreateDummy();
        var grid = new TileGrid(64, 64, []);
        const uint baseCooldown = 3;
        const double factor = 0.4d;
        var slowed = CombatTuning.SlowedStepCooldownTicks(baseCooldown, factor);
        Assert.Equal(8u, slowed);

        // A window long enough to contain two steps' worth of slowed cadence.
        const uint slowDuration = 20;
        entity.ApplyAttackMovementSlowAuthored(0, 0, slowDuration, factor, pastWindowTicks: 64, futureLeadTicks: 4);

        // First step at tick 0 is accepted (inside the window, factor != 0 so not blocked) and schedules the next
        // step a SLOWED cooldown out.
        Assert.True(entity.TryStep(Direction8.E, 0, baseCooldown, grid, out _));

        // The next step is NOT eligible at base cadence (tick 3) — it is slowed — but IS at the slowed cadence (8).
        Assert.False(entity.TryStep(Direction8.E, baseCooldown, baseCooldown, grid, out _));
        Assert.True(entity.TryStep(Direction8.E, slowed, baseCooldown, grid, out _));
    }

    [Fact]
    public void SwingSlowWindowIsAFloorNeverShortensAnActiveWindow()
    {
        // SWING-SLOW: like the old root, a new swing only EXTENDS the slow window — it never pulls a later window end
        // earlier. Open a long window [0, 30), then a second swing with a SHORTER window anchored at tick 2 (ends at
        // 6): the earlier-ending window must NOT shorten the active one. With factor 0 (block) the entity stays
        // blocked until the LONGER window end (30), proving the floor.
        var entity = CreateDummy();
        var grid = new TileGrid(16, 16, []);

        entity.ApplyAttackMovementSlowAuthored(0, 0, slowDurationTicks: 30, factor: 0d, pastWindowTicks: 64, futureLeadTicks: 4);
        entity.ApplyAttackMovementSlowAuthored(2, 2, slowDurationTicks: 4, factor: 0d, pastWindowTicks: 64, futureLeadTicks: 4);

        Assert.False(entity.TryStep(Direction8.E, 29, stepCooldownTicks: 3, grid, out _)); // still inside [0,30)
        Assert.True(entity.TryStep(Direction8.E, 30, stepCooldownTicks: 3, grid, out _));  // window elapsed
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
