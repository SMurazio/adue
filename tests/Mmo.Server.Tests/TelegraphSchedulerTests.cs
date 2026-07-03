using System;
using System.Collections.Generic;
using Mmo.Server.Runtime;
using Mmo.Shared.Domain;
using Mmo.Shared.Domain.Actions;
using Xunit;

namespace Mmo.Server.Tests;

// TELEGRAPH T1 (docs/ability-telegraph-sync-design.md + todo/N-iframe-gate-choke-point.md): headless tests for the
// scheduled-telegraph engine + THE player-damage choke point. Driven against the REAL pieces GameServer wires — a
// WorldState's spatial gather, the real ServerActionExecutor (with the SHIPPED dodge-roll def and its i-frame
// window), the real PlayerDamageGate, and a TelegraphScheduler routed through that gate — in the SAME per-tick order
// TickCore runs (executor StepAll, then ResolveDue). These pin the deadline model's core: a telegraph resolves at
// EXACTLY its tick T against positions AT T (never at cast — inside-at-cast escapes by stepping out; outside-at-cast
// is caught by stepping in); a mid-dodge-roll victim inside the shape takes NOTHING because the REAL gate negates it
// (delete the i-frame check from PlayerDamageGate and these fail — the seam todo/N-iframe-gate-choke-point.md
// flagged as untested); the same gate pins the monster-melee caller's order (dead-guard → i-frames → ApplyDamage);
// a telegraph outlives a despawned caster (the pinned decision); and the pending list never leaks.
public sealed class TelegraphSchedulerTests
{
    private const int TickRate = 20;
    private const double BodyRadius = CollisionDefaults.BodyRadius; // 0.5

    private static MovementActionDef DodgeRollDef => MovementActionRegistry.Default.Get(ActionId.DodgeRoll);

    // The action executor wired exactly like GameServer / the action suites: the REAL shared wall derivation over an
    // open grid + the world's apply/bucket-migrate seam, so a dodge-roll started here is the genuine article.
    private static ServerActionExecutor CreateExecutor(WorldState world)
    {
        var grid = new TileGrid(64, 64, []);
        return new ServerActionExecutor(
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
    }

    // The engine wired exactly like GameServer's ctor: the gate closes over the REAL executor's i-frame oracle + a
    // recording landed tail (the headless stand-in for BroadcastDamageEvent + the death edge), and the scheduler
    // routes damage through gate.TryDamagePlayer — the literal choke-point method group, not a re-implementation.
    private static (TelegraphScheduler Scheduler, PlayerDamageGate Gate, List<(WorldEntity Victim, int Amount, string Source)> Landed)
        CreateEngine(WorldState world, ServerActionExecutor executor)
    {
        var landed = new List<(WorldEntity Victim, int Amount, string Source)>();
        var gate = new PlayerDamageGate(
            executor.HasActiveIFrames,
            (victim, amount, source) => landed.Add((victim, amount, source)));
        var scheduler = new TelegraphScheduler(world.GatherInterestCandidates, gate.TryDamagePlayer);
        return (scheduler, gate, landed);
    }

    // Repositions an entity to a continuous point, keeping the spatial grid's bucket in sync (the same
    // apply + OnEntityMoved bookkeeping every move path runs) — the "player walked during the windup" primitive.
    private static void MoveTo(WorldState world, WorldEntity entity, WorldVector position)
    {
        var previous = entity.TileCoord;
        if (entity.ApplyResolvedMove(position))
        {
            world.OnEntityMoved(entity, previous);
        }
    }

    [Fact]
    public void CircleMembership_IsInclusiveEuclidean()
    {
        var shape = TelegraphShape.Circle(new WorldVector(10d, 10d), 2d);

        Assert.True(shape.Contains(new WorldVector(10d, 10d)));       // centre
        Assert.True(shape.Contains(new WorldVector(11.5d, 10d)));     // inside
        Assert.True(shape.Contains(new WorldVector(12d, 10d)));       // exactly on the rim — INCLUSIVE
        Assert.False(shape.Contains(new WorldVector(12.001d, 10d)));  // just outside
        Assert.False(shape.Contains(new WorldVector(11.5d, 11.5d)));  // Euclidean: √(1.5²+1.5²) ≈ 2.12 > 2 (a
                                                                      // Chebyshev-2 corner is OUT of a radius-2 circle)
        Assert.Equal(2d, shape.BoundingRadius);
    }

    [Fact]
    public void ResolvesAtExactlyTickT_PlayerInsideAtT_IsHitOnce()
    {
        var world = new WorldState();
        var player = world.AddTransient(1, EntityKind.Player, "Hero", new TileCoord(32, 32), Direction8.S);
        var (scheduler, _, landed) = CreateEngine(world, CreateExecutor(world));

        // Scheduled at tick 20, resolving at tick 50 — the deadline form.
        scheduler.Schedule(999, TelegraphShape.Circle(player.Position, 2d), resolveTick: 50, damage: 15, source: "test slam");
        Assert.Equal(1, scheduler.PendingCount);

        // Every tick strictly BEFORE T: nothing resolves, nothing lands, the entry stays pending.
        for (uint tick = 21; tick < 50; tick++)
        {
            scheduler.ResolveDue(tick);
            Assert.Empty(landed);
            Assert.Equal(1, scheduler.PendingCount);
            Assert.Equal(100, player.Stats.Health);
        }

        // AT tick T: the hit lands and the entry leaves the pending list.
        scheduler.ResolveDue(50);
        var hit = Assert.Single(landed);
        Assert.Same(player, hit.Victim);
        Assert.Equal(15, hit.Amount);
        Assert.Equal("test slam", hit.Source);
        Assert.Equal(85, player.Stats.Health);
        Assert.Equal(0, scheduler.PendingCount);

        // AFTER T: the telegraph is gone — no double resolve, no second hit.
        scheduler.ResolveDue(51);
        Assert.Single(landed);
        Assert.Equal(85, player.Stats.Health);
    }

    [Fact]
    public void InsideAtCast_SteppedOutByT_TakesNothing()
    {
        var world = new WorldState();
        var player = world.AddTransient(1, EntityKind.Player, "Hero", new TileCoord(32, 32), Direction8.S);
        var (scheduler, _, landed) = CreateEngine(world, CreateExecutor(world));

        // Locked at the player's CAST-TIME position — the player then walks 5 units east during the windup.
        var origin = player.Position;
        scheduler.Schedule(999, TelegraphShape.Circle(origin, 2d), resolveTick: 50, damage: 15, source: "test slam");
        MoveTo(world, player, origin + new WorldVector(5d, 0d));

        scheduler.ResolveDue(50);

        // Membership is judged at T, not at cast: the dodge worked.
        Assert.Empty(landed);
        Assert.Equal(100, player.Stats.Health);
        Assert.Equal(0, scheduler.PendingCount); // resolved (on nobody) and removed — a miss never lingers.
    }

    [Fact]
    public void OutsideAtCast_SteppedInByT_IsHit()
    {
        var world = new WorldState();
        var player = world.AddTransient(1, EntityKind.Player, "Hero", new TileCoord(40, 32), Direction8.S);
        var (scheduler, _, landed) = CreateEngine(world, CreateExecutor(world));

        // Locked 8 units away from the player at cast — the player then walks INTO it during the windup.
        var origin = new WorldVector(32d, 32d);
        scheduler.Schedule(999, TelegraphShape.Circle(origin, 2d), resolveTick: 50, damage: 15, source: "test slam");
        MoveTo(world, player, origin + new WorldVector(0.5d, 0d));

        scheduler.ResolveDue(50);

        // Positions AT T, never at cast: walking in eats the hit even though the cast-time position was safe.
        Assert.Single(landed);
        Assert.Equal(85, player.Stats.Health);
    }

    [Fact]
    public void MidDodgeRollInsideShapeAtT_TakesNothing_ThroughTheRealGate()
    {
        // THE choke-point pin (todo/N-iframe-gate-choke-point.md): two players inside the circle at T — one mid
        // dodge-roll (the SHIPPED def, started on the REAL executor), one standing still. The roller takes NOTHING
        // because PlayerDamageGate consults the executor's i-frame window; the stander takes the hit, proving the
        // telegraph resolved. Deleting the i-frame check from PlayerDamageGate (the REAL production gate — no test
        // lambda re-implements it) fails this test.
        var world = new WorldState();
        var roller = world.AddTransient(1, EntityKind.Player, "Roller", new TileCoord(32, 32), Direction8.S);
        roller.SetSpeedUnitsPerSecond(5d);
        var stander = world.AddTransient(2, EntityKind.Player, "Stander", new TileCoord(33, 32), Direction8.S);
        var executor = CreateExecutor(world);
        var (scheduler, _, landed) = CreateEngine(world, executor);

        var def = DodgeRollDef;
        Assert.True(def.HasIFrameWindow); // the shipped roll ships a real window ([1, 4])

        // Resolve tick T = 50; the roll starts at T-2, so elapsed-at-T = 2 ∈ [IFrameStartTick, IFrameEndTick].
        const uint resolveTick = 50;
        var rollStart = resolveTick - 2;
        Assert.InRange(2u, def.IFrameStartTick, def.IFrameEndTick);

        // Circle radius 3 centred between them — big enough that the roll's short dash cannot leave it by T (the
        // in-shape assertion below keeps the test honest about that).
        scheduler.Schedule(999, TelegraphShape.Circle(new WorldVector(32.5d, 32d), 3d), resolveTick, damage: 15, source: "test slam");

        // Drive the ticks in TickCore's order: actions step, then due telegraphs resolve.
        for (var tick = rollStart; tick <= resolveTick; tick++)
        {
            if (tick == rollStart)
            {
                Assert.True(executor.TryStart(roller, def, new WorldVector(1d, 0d), tick));
            }

            executor.StepAll(world, tick);
            scheduler.ResolveDue(tick);
        }

        // Both were inside the shape at T…
        Assert.True(TelegraphShape.Circle(new WorldVector(32.5d, 32d), 3d).Contains(roller.Position),
            "test setup: the roller left the shape before T — enlarge the circle.");
        // …but only the NON-rolling player was damaged: the real gate negated the roller's hit.
        var hit = Assert.Single(landed);
        Assert.Same(stander, hit.Victim);
        Assert.Equal(100, roller.Stats.Health);
        Assert.Equal(85, stander.Stats.Health);
        Assert.Equal(0, scheduler.PendingCount);
    }

    [Fact]
    public void MonsterMeleeSeam_GateOrderIsDeadGuardThenIFramesThenDamage()
    {
        // The OTHER caller of the same choke point: ApplyMonsterAttack routes its damage through
        // PlayerDamageGate.TryDamagePlayer (face-the-victim, then the gate). This pins the gate's order at the REAL
        // seam — not a test-local ResolveHit lambda (the exact miss the Phase-D review flagged): a mid-roll victim is
        // negated, a post-window victim is hit, a downed (dead-session) victim is guarded, a 0-HP victim is a no-op,
        // and a non-player victim is refused (this is the PLAYER-damage choke point).
        var world = new WorldState();
        var executor = CreateExecutor(world);
        var (_, gate, landed) = CreateEngine(world, executor);

        var victim = world.AddTransient(1, EntityKind.Player, "Hero", new TileCoord(32, 32), Direction8.S);
        victim.SetSpeedUnitsPerSecond(5d);
        var def = DodgeRollDef;

        // Mid-roll (elapsed 1 ∈ [1, 4]): NEGATED by the real executor-backed gate.
        Assert.True(executor.TryStart(victim, def, new WorldVector(1d, 0d), serverTick: 100));
        executor.StepAll(world, 101);
        Assert.False(gate.TryDamagePlayer(victim, 10, serverTick: 101, source: "Monster 7"));
        Assert.Equal(100, victim.Stats.Health);
        Assert.Empty(landed);

        // Run the roll out; past the window (and with the action ended) the SAME call lands.
        for (uint tick = 102; tick <= 100 + def.DurationTicks; tick++)
        {
            executor.StepAll(world, tick);
        }

        Assert.True(gate.TryDamagePlayer(victim, 10, serverTick: 100 + def.DurationTicks, source: "Monster 7"));
        Assert.Equal(90, victim.Stats.Health);
        Assert.Single(landed);

        // Dead-guard: a downed session's entity takes no further hits (and the landed tail does not run again).
        var session = new ClientSession(null!);
        var downed = new WorldEntity(
            id: 500, networkId: 500, EntityKind.Player, new TileCoord(30, 30), Direction8.S,
            "Downed", Guid.NewGuid(), ownerSession: session, isDurable: true);
        Assert.True(session.MarkDead(serverTick: 200, respawnDelayTicks: 100));
        Assert.False(gate.TryDamagePlayer(downed, 10, serverTick: 201, source: "Monster 7"));
        Assert.Equal(100, downed.Stats.Health);

        // Players only: the choke point refuses a non-player victim outright.
        var monster = world.AddTransient(3, EntityKind.Monster, "Slime", new TileCoord(31, 31), Direction8.S);
        Assert.False(gate.TryDamagePlayer(monster, 10, serverTick: 202, source: "Monster 7"));
        Assert.Equal(100, monster.Stats.Health);
        Assert.Single(landed); // no extra landed entries from the guarded/refused calls.
    }

    [Fact]
    public void CasterDespawnedMidWindup_TelegraphStillResolves()
    {
        // The pinned lifetime decision: the wound-up danger is already in the world — killing/despawning the caster
        // mid-windup does NOT defuse the telegraph (resolve never dereferences the caster, so nothing dangles).
        var world = new WorldState();
        var caster = world.AddTransient(1, EntityKind.Monster, "Slime", new TileCoord(30, 32), Direction8.S);
        var player = world.AddTransient(2, EntityKind.Player, "Hero", new TileCoord(32, 32), Direction8.S);
        var (scheduler, _, landed) = CreateEngine(world, CreateExecutor(world));

        scheduler.Schedule(caster.Id, TelegraphShape.Circle(player.Position, 2d), resolveTick: 50, damage: 15, source: "Slime slam");
        Assert.True(world.Remove(caster.Id, out _)); // the caster dies mid-windup

        scheduler.ResolveDue(50);

        Assert.Single(landed);
        Assert.Equal(85, player.Stats.Health);
        Assert.Equal(0, scheduler.PendingCount);
    }

    [Fact]
    public void MonstersAndDeadPlayersInsideTheShape_AreUnaffected()
    {
        // No friendly fire (mirrors ApplyMonsterAttack's targeting): the resolve damages alive PLAYERS only — a
        // monster and a 0-HP player inside the circle are untouched while a live player beside them is hit.
        var world = new WorldState();
        var player = world.AddTransient(1, EntityKind.Player, "Hero", new TileCoord(32, 32), Direction8.S);
        var monster = world.AddTransient(2, EntityKind.Monster, "Slime", new TileCoord(32, 33), Direction8.S);
        var downed = world.AddTransient(3, EntityKind.Player, "Downed", new TileCoord(33, 32), Direction8.S);
        downed.ApplyDamage(100); // already at 0 HP — not a re-hit target
        var (scheduler, _, landed) = CreateEngine(world, CreateExecutor(world));

        scheduler.Schedule(999, TelegraphShape.Circle(new WorldVector(32d, 32d), 2d), resolveTick: 50, damage: 15, source: "test slam");
        scheduler.ResolveDue(50);

        var hit = Assert.Single(landed);
        Assert.Same(player, hit.Victim);
        Assert.Equal(100, monster.Stats.Health);
        Assert.Equal(0, downed.Stats.Health);
    }

    [Fact]
    public void PendingList_ResolvesEachEntryOnItsOwnTick_AndNeverLeaks()
    {
        // Three telegraphs with distinct deadlines (two sharing one): each resolves on its own tick, the count
        // decrements exactly as entries resolve, and nothing remains after the last deadline (the leak sentinel).
        var world = new WorldState();
        var player = world.AddTransient(1, EntityKind.Player, "Hero", new TileCoord(32, 32), Direction8.S);
        player.SetMaxHealthFull(1000); // room for every hit
        var (scheduler, _, landed) = CreateEngine(world, CreateExecutor(world));

        var shape = TelegraphShape.Circle(player.Position, 2d);
        scheduler.Schedule(999, shape, resolveTick: 30, damage: 1, source: "a");
        scheduler.Schedule(999, shape, resolveTick: 40, damage: 2, source: "b");
        scheduler.Schedule(999, shape, resolveTick: 40, damage: 3, source: "c");
        Assert.Equal(3, scheduler.PendingCount);

        for (uint tick = 1; tick <= 100; tick++)
        {
            scheduler.ResolveDue(tick);
            var expected = tick < 30 ? 3 : tick < 40 ? 2 : 0;
            Assert.Equal(expected, scheduler.PendingCount);
        }

        Assert.Equal(3, landed.Count);
        Assert.Equal(1000 - 1 - 2 - 3, player.Stats.Health);
        // Same-deadline entries resolved in schedule order (deterministic).
        Assert.Equal("a", landed[0].Source);
        Assert.Equal("b", landed[1].Source);
        Assert.Equal("c", landed[2].Source);
    }
}
