using System;
using Mmo.Server.Runtime;
using Mmo.Shared.Domain;
using Mmo.Shared.Domain.Actions;
using Xunit;

namespace Mmo.Server.Tests;

// MONSTER-BEHAVIOR P5 (docs/monster-behavior-design.md): headless tests for the gnoll's first ABILITY — a CHARGE (a
// fast forward dash through the shared ServerActionExecutor to close the gap to its target). These pin: a charge-
// configured monster with a target just outside attack range but within the trigger range STARTS a charge and, while
// the dash is active, the glide does NOT additionally move it (the self-guard kills the double-move the executor +
// glide would otherwise produce the same tick); a non-charger never charges (inert); a WOUNDED skirmisher FLEES and
// never charges (flee precedence); a target already in attack range / beyond the trigger range does NOT charge; and the
// charge def is a GROUNDED (jumpHeight 0) ActionId.Charge forward arc whose cooldown the EXECUTOR enforces. Driven
// directly against a WorldState + TileGrid + the real GlideLocomotion + ServerActionExecutor (no network/GameServer),
// wiring the tick order GameServer uses: behavior.StepMonster (decides + may trigger the charge) THEN executor.StepAll
// (drives the dash) — the SAME order that makes the glide self-guard matter (StepMonsterAi runs before StepAll).
public sealed class MonsterChargeTests
{
    private const int GridSize = 64;
    private const uint StepCooldownTicks = 3;     // the project's base cadence (the glide's watchdog window).
    private const double BodyRadius = 0.5d;        // the player body radius the monster also collides at.
    private const int TickRate = 20;               // server tick rate (fixes dt for the glide + the dash arc).
    private const double WalkSpeed = 4.0d;         // a non-zero walk speed (a glider can't move without one).

    // Charge tuning under test: dash 4 units over 6 ticks (a fast ~13 u/s dash vs the ~4 u/s walk), re-charge cooldown
    // 80 ticks (long enough that a second charge within a test window is declined), fire when the gap is 1.5..7 units.
    private const double ChargeDistance = 4.0d;
    private const uint ChargeDuration = 6;
    private const uint ChargeCooldown = 80;
    private const double TriggerRange = 7.0d;
    private const double AttackRange = 1.5d;

    private static TileGrid OpenGrid() => new(GridSize, GridSize, []);

    private static double Distance(WorldVector a, WorldVector b) => (a - b).Length;

    private static WorldEntity SpawnMonster(WorldState world, TileCoord tile, int health, int maxHealth, uint networkId = 1)
    {
        var monster = world.AddTransient(networkId, EntityKind.Monster, "Monster", tile, Direction8.S);
        monster.SetSpeedUnitsPerSecond(WalkSpeed);
        monster.SetMaxHealthFull(maxHealth);
        if (health < maxHealth)
        {
            monster.ApplyDamage(maxHealth - health);
        }

        return monster;
    }

    // The action executor wired exactly like GameServer / the hop tests: the SAME shared wall query + body radius +
    // apply seam ordinary movement uses. The charge (and the glide self-guard's IsActive check) run on this instance.
    private static ServerActionExecutor CreateExecutor(TileGrid grid, WorldState world)
        => new(
            TickRate,
            () => BodyRadius,
            grid.QueryNearbyWalls,
            (entity, landing) =>
            {
                var previous = entity.TileCoord;
                var crossed = entity.ApplyResolvedMove(landing);
                if (crossed)
                {
                    world.OnEntityMoved(entity, previous);
                }

                return crossed;
            });

    // A GlideLocomotion wired like GameServer, with the P5 self-guard reading the SAME executor the charge runs on — so
    // while a charge dash is active the glide returns OnCooldown + makes no move (no double-move).
    private static GlideLocomotion CreateGlide(TileGrid grid, WorldState world, ServerActionExecutor executor)
        => new(
            () => BodyRadius,
            grid.QueryNearbyWalls,
            (entity, landing) =>
            {
                var previous = entity.TileCoord;
                var crossed = entity.ApplyResolvedMove(landing);
                if (crossed)
                {
                    world.OnEntityMoved(entity, previous);
                }

                return crossed;
            },
            TickRate,
            executor.IsActive);

    // The brain's TryChargeDelegate, mirroring GameServer.BeginMonsterCharge: build a GROUNDED (jumpHeight 0) forward
    // arc Charge def + executor.TryStart. Returns whether the charge actually started (false = on cooldown / already acting).
    private static BasicRoamerBehavior.TryChargeDelegate CreateCharge(ServerActionExecutor executor)
        => (WorldEntity monster, WorldVector heading, uint serverTick) =>
            executor.TryStart(monster, ChargeDef(), heading, serverTick);

    private static MovementActionDef ChargeDef() => MovementActionRegistry.BuildForwardArcJump(
        ActionId.Charge,
        durationTicks: ChargeDuration,
        jumpHeight: 0d,
        forwardDistanceUnits: ChargeDistance,
        cooldownTicks: ChargeCooldown,
        animationId: 2);

    // Builds a behavior (skirmisher or basic roamer) wired to a live player target + a hit counter + the charge dep,
    // mirroring GameServer's continuous combat path (findTarget by Euclidean Position within the coarse gather radius;
    // tryResolve to the live Position + alive; attack = count + ApplyDamage).
    private static BasicRoamerBehavior CreateBehavior(
        int seed, TileGrid grid, WorldState world, WorldEntity player, int[] hitCounter,
        BasicRoamerBehavior.TryChargeDelegate tryCharge, bool skirmisher)
    {
        BasicRoamerBehavior.FindTargetDelegate findTarget =
            (WorldEntity monster, int gatherRadius, out ulong id, out WorldVector pos) =>
            {
                if (!world.TryGet(player.Id, out var p) || p.Stats.Health <= 0)
                {
                    id = 0;
                    pos = default;
                    return false;
                }

                var cheb = Math.Max(
                    Math.Abs(p.TileCoord.X - monster.TileCoord.X),
                    Math.Abs(p.TileCoord.Y - monster.TileCoord.Y));
                if (cheb > gatherRadius)
                {
                    id = 0;
                    pos = default;
                    return false;
                }

                id = p.Id;
                pos = p.Position;
                return true;
            };

        BasicRoamerBehavior.TryResolveTargetDelegate tryResolve =
            (ulong id, out WorldVector pos, out bool alive) =>
            {
                if (world.TryGet(id, out var e))
                {
                    pos = e.Position;
                    alive = e.Stats.Health > 0;
                    return true;
                }

                pos = default;
                alive = false;
                return false;
            };

        BasicRoamerBehavior.AttackDelegate attack =
            (WorldEntity monster, ulong id, int damage) =>
            {
                hitCounter[0]++;
                if (world.TryGet(id, out var e))
                {
                    e.ApplyDamage(damage);
                }
            };

        return skirmisher
            ? new SkirmisherBehavior(seed, grid.IsWalkable, findTarget, tryResolve, attack, tryCharge)
            : new BasicRoamerBehavior(seed, grid.IsWalkable, findTarget, tryResolve, attack, tryCharge);
    }

    // Combat tunables with the charge config (and a configurable flee threshold). Big de-aggro/leash so the chaser
    // never gives up + returns home mid-assertion.
    private static MonsterAiTunables Tunables(
        bool chargeEnabled,
        double fleeHealthPct = 0d,
        double aggroRadius = 8d,
        double deaggroRadius = 60d,
        double chaseLeash = 60d,
        uint attackCooldownTicks = 20,
        uint aggroScanInterval = 1)
        => new(
            RoamRadius: 4d,
            PauseMinTicks: 100,
            PauseMaxTicks: 100,
            AggroRadius: aggroRadius,
            DeaggroRadius: deaggroRadius,
            ChaseLeash: chaseLeash,
            AttackRangeUnits: AttackRange,
            AttackDamage: 10,
            AttackCooldownTicks: attackCooldownTicks,
            AggroScanIntervalTicks: aggroScanInterval,
            FleeHealthPct: fleeHealthPct,
            ChargeEnabled: chargeEnabled,
            ChargeDistanceUnits: ChargeDistance,
            ChargeTriggerRangeUnits: TriggerRange,
            ChargeCooldownTicks: ChargeCooldown);

    [Fact]
    public void ChargeConfigured_TargetOutsideAttackButWithinTrigger_StartsCharge_NoDoubleMove()
    {
        // Player 5 units east — outside attack range (1.5) but inside the trigger range (7). The monster aggros and
        // CHARGES to close the gap. While the dash is active, behavior.StepMonster must NOT move the monster (the glide
        // self-guards) — only executor.StepAll drives it. Then it lands in attack range and hits the player.
        var grid = OpenGrid();
        var world = new WorldState();
        var monster = SpawnMonster(world, new TileCoord(32, 32), health: 100, maxHealth: 100);
        var player = world.AddTransient(2, EntityKind.Player, "Hero", new TileCoord(37, 32), Direction8.S);
        var executor = CreateExecutor(grid, world);
        var glide = CreateGlide(grid, world, executor);
        var hits = new int[1];
        var behavior = CreateBehavior(7, grid, world, player, hits, CreateCharge(executor), skirmisher: false);
        behavior.Register(monster, serverTick: 0, pauseMinTicks: 100, pauseMaxTicks: 100, aggroScanIntervalTicks: 1);

        var charged = false;
        for (uint tick = 1; tick <= 80; tick++)
        {
            var wasActive = executor.IsActive(monster.Id);
            var posBeforeStepMonster = monster.Position;
            behavior.StepMonster(monster, tick, StepCooldownTicks, Tunables(chargeEnabled: true), glide);

            // NO DOUBLE-MOVE: on any tick the dash was already active, StepMonster (with the glide) must not move the
            // monster — the executor's StepAll (below) is the SOLE mover mid-dash.
            if (wasActive)
            {
                Assert.Equal(posBeforeStepMonster, monster.Position);
            }

            if (executor.IsActive(monster.Id))
            {
                charged = true;
            }

            executor.StepAll(world, tick);
        }

        Assert.True(charged, "the monster never started a charge despite a target in the trigger band.");
        Assert.True(monster.Position.X > 35.5d,
            $"the charge did not close the gap east (x={monster.Position.X:F3}, expected ~36 after a 4-unit dash).");
        Assert.True(Distance(monster.Position, player.Position) <= AttackRange + 1e-6,
            "the charge did not bring the monster into attack range of the player.");
        Assert.True(hits[0] > 0, "the monster never attacked after closing the gap with the charge.");
        Assert.Equal(ActionId.None, executor.ActiveAction(monster.Id)); // the dash finished within the window.
    }

    [Fact]
    public void NoChargeConfig_NeverCharges_EvenWithAChargeDelegateWired()
    {
        // ChargeEnabled false (a basic roamer / slime): even with a working charge delegate wired, the brain GATE keeps
        // it inert — it never calls tryCharge, so no action ever starts. It still chases via the glide.
        var grid = OpenGrid();
        var world = new WorldState();
        var monster = SpawnMonster(world, new TileCoord(32, 32), health: 100, maxHealth: 100);
        var player = world.AddTransient(2, EntityKind.Player, "Hero", new TileCoord(37, 32), Direction8.S);
        var executor = CreateExecutor(grid, world);
        var glide = CreateGlide(grid, world, executor);
        var hits = new int[1];
        var behavior = CreateBehavior(7, grid, world, player, hits, CreateCharge(executor), skirmisher: false);
        behavior.Register(monster, serverTick: 0, pauseMinTicks: 100, pauseMaxTicks: 100, aggroScanIntervalTicks: 1);

        var startDist = Distance(monster.Position, player.Position);
        for (uint tick = 1; tick <= 40; tick++)
        {
            behavior.StepMonster(monster, tick, StepCooldownTicks, Tunables(chargeEnabled: false), glide);
            Assert.False(executor.IsActive(monster.Id), $"a non-charger started an action at tick {tick}.");
            executor.StepAll(world, tick);
        }

        Assert.True(Distance(monster.Position, player.Position) < startDist - 0.5d,
            "the non-charger should still chase the player via the glide (it just never charges).");
    }

    [Fact]
    public void WoundedSkirmisher_Flees_DoesNotCharge_FleePrecedence()
    {
        // A wounded skirmisher (HP 20 <= 0.3*100) with the charge configured + a player in the trigger band: flee takes
        // precedence (the flee hook runs BEFORE the charge trigger), so it GLIDES AWAY and never charges.
        var grid = OpenGrid();
        var world = new WorldState();
        var home = WorldVector.FromTile(new TileCoord(32, 32));
        var monster = SpawnMonster(world, home.ToTileRounded(), health: 20, maxHealth: 100);
        var player = world.AddTransient(2, EntityKind.Player, "Hero", new TileCoord(37, 32), Direction8.S);
        var executor = CreateExecutor(grid, world);
        var glide = CreateGlide(grid, world, executor);
        var hits = new int[1];
        var behavior = CreateBehavior(7, grid, world, player, hits, CreateCharge(executor), skirmisher: true);
        behavior.Register(monster, serverTick: 0, pauseMinTicks: 100, pauseMaxTicks: 100, aggroScanIntervalTicks: 1);

        var startDist = Distance(monster.Position, player.Position);
        for (uint tick = 1; tick <= 40; tick++)
        {
            behavior.StepMonster(monster, tick, StepCooldownTicks, Tunables(chargeEnabled: true, fleeHealthPct: 0.3d), glide);
            Assert.False(executor.IsActive(monster.Id), $"a fleeing wounded skirmisher charged at tick {tick}.");
            executor.StepAll(world, tick);
        }

        Assert.True(monster.Position.X < home.X - 0.5d,
            $"the wounded skirmisher did not flee WEST away from the eastern player (x={monster.Position.X:F3}).");
        Assert.True(Distance(monster.Position, player.Position) > startDist + 0.5d,
            "the wounded skirmisher did not increase its distance from the target while fleeing.");
        Assert.Equal(0, hits[0]);
    }

    [Fact]
    public void TargetInAttackRange_DoesNotCharge_Attacks()
    {
        // Player adjacent (1 unit < attack range 1.5): the charge trigger requires the target be OUT of attack range, so
        // it does NOT charge — it stops and attacks.
        var grid = OpenGrid();
        var world = new WorldState();
        var monster = SpawnMonster(world, new TileCoord(32, 32), health: 100, maxHealth: 100);
        var player = world.AddTransient(2, EntityKind.Player, "Hero", new TileCoord(33, 32), Direction8.S);
        var executor = CreateExecutor(grid, world);
        var glide = CreateGlide(grid, world, executor);
        var hits = new int[1];
        var behavior = CreateBehavior(7, grid, world, player, hits, CreateCharge(executor), skirmisher: false);
        behavior.Register(monster, serverTick: 0, pauseMinTicks: 100, pauseMaxTicks: 100, aggroScanIntervalTicks: 1);

        for (uint tick = 1; tick <= 40; tick++)
        {
            behavior.StepMonster(monster, tick, StepCooldownTicks, Tunables(chargeEnabled: true), glide);
            Assert.False(executor.IsActive(monster.Id), $"charged at an in-range target at tick {tick}.");
            executor.StepAll(world, tick);
        }

        Assert.True(hits[0] > 0, "an in-range monster should attack, not charge.");
    }

    [Fact]
    public void TargetBeyondTriggerRange_DoesNotCharge_Approaches()
    {
        // Player 10 units east — beyond the trigger range (7): the monster approaches via the glide but does NOT charge
        // while the gap exceeds the trigger range. Asserted per tick while still beyond trigger.
        var grid = OpenGrid();
        var world = new WorldState();
        var monster = SpawnMonster(world, new TileCoord(32, 32), health: 100, maxHealth: 100);
        var player = world.AddTransient(2, EntityKind.Player, "Hero", new TileCoord(42, 32), Direction8.S);
        var executor = CreateExecutor(grid, world);
        var glide = CreateGlide(grid, world, executor);
        var hits = new int[1];
        var behavior = CreateBehavior(7, grid, world, player, hits, CreateCharge(executor), skirmisher: false);
        behavior.Register(monster, serverTick: 0, pauseMinTicks: 100, pauseMaxTicks: 100, aggroScanIntervalTicks: 1);

        var startDist = Distance(monster.Position, player.Position);
        for (uint tick = 1; tick <= 10; tick++)
        {
            // Still beyond the trigger range every tick in this short window (it only closes ~0.2/tick from 10).
            Assert.True(Distance(monster.Position, player.Position) > TriggerRange,
                "test setup: the monster closed within the trigger range sooner than expected.");
            // aggroRadius 30 so the monster actually CHASES the 10-unit-away player (default 8 < 10 → it would never
            // aggro and would only roam randomly, making "approached" RNG-flaky). It closes ~0.2/tick (10→8 over 10
            // ticks), staying beyond the trigger range (7) the whole window — so it approaches but never charges.
            behavior.StepMonster(monster, tick, StepCooldownTicks, Tunables(chargeEnabled: true, aggroRadius: 30d), glide);
            Assert.False(executor.IsActive(monster.Id), $"charged from beyond the trigger range at tick {tick}.");
            executor.StepAll(world, tick);
        }

        Assert.True(Distance(monster.Position, player.Position) < startDist - 0.5d,
            "the monster should approach a beyond-trigger target via the glide.");
    }

    [Fact]
    public void ChargeDef_IsGroundedForwardArc_WithChargeId_AndExecutorEnforcesTheCooldown()
    {
        // The charge def is a GROUNDED (jumpHeight 0) ActionId.Charge forward arc of the configured distance + cooldown.
        // Driving it: the monster dashes ~ChargeDistance forward with VerticalOffset staying 0 (grounded), and a SECOND
        // charge within the cooldown window is DECLINED by the executor (CanStart), accepted again once it elapses.
        var grid = OpenGrid();
        var world = new WorldState();
        var monster = SpawnMonster(world, new TileCoord(32, 32), health: 100, maxHealth: 100);
        var executor = CreateExecutor(grid, world);
        var heading = new WorldVector(1d, 0d);
        var def = ChargeDef();

        Assert.Equal(ActionId.Charge, def.Id);
        Assert.Equal(0d, def.JumpHeight);                        // grounded — no Z arc.
        Assert.Equal(HorizontalMode.ForwardArc, def.HorizontalMode);
        Assert.Equal(ChargeDistance, def.ForwardDistanceUnits, 6);
        Assert.Equal(ChargeCooldown, def.CooldownTicks);

        var originX = monster.Position.X;
        const uint start = 100;
        Assert.True(executor.TryStart(monster, def, heading, start)); // tick 0 applied (grounded, no XY).

        // Drive the dash to completion; VerticalOffset must stay 0 (grounded) the whole way.
        var endTick = start;
        while (executor.IsActive(monster.Id))
        {
            endTick++;
            executor.Step(monster, endTick);
            Assert.Equal(0d, monster.VerticalOffset, 9);
        }

        Assert.Equal(ChargeDistance, monster.Position.X - originX, 6); // dashed the full distance forward (open field).
        Assert.Equal(0d, monster.VerticalOffset, 9);

        // COOLDOWN: a re-charge at the end tick (and just before the cooldown elapses) is declined; allowed once it does.
        Assert.False(executor.TryStart(monster, def, heading, endTick));
        Assert.False(executor.TryStart(monster, def, heading, endTick + ChargeCooldown - 1));
        Assert.True(executor.TryStart(monster, def, heading, endTick + ChargeCooldown));
    }

    [Fact]
    public void ChargeEnabled_RequiresBothTheAbilityIdAndAPositiveCooldown()
    {
        // The composition gate: a type is charge-enabled ONLY if it composed the "charge" ability AND authored a
        // positive cooldown. The ability without a cooldown (or a cooldown without the ability) is inert.
        var both = new MonsterType("g", "G") { AbilityIds = ["charge"], ChargeCooldownMs = 4000 };
        Assert.True(MonsterTypeRegistry.ChargeEnabled(both));

        var abilityOnly = new MonsterType("g", "G") { AbilityIds = ["charge"], ChargeCooldownMs = 0 };
        Assert.False(MonsterTypeRegistry.ChargeEnabled(abilityOnly));

        var cooldownOnly = new MonsterType("g", "G") { AbilityIds = [], ChargeCooldownMs = 4000 };
        Assert.False(MonsterTypeRegistry.ChargeEnabled(cooldownOnly));

        var caseInsensitive = new MonsterType("g", "G") { AbilityIds = ["Charge"], ChargeCooldownMs = 4000 };
        Assert.True(MonsterTypeRegistry.ChargeEnabled(caseInsensitive));
    }
}
