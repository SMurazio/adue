using System;
using System.Collections.Generic;
using Mmo.Server.Runtime;
using Mmo.Shared.Domain;
using Mmo.Shared.Domain.Actions;
using Mmo.Shared.Protocol;
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
        Assert.False(shape.Contains(new WorldVector(12.4d, 10d)));    // CENTER-POINT membership (decided): center out
                                                                      // by 0.4 — a 0.5-radius body clips the rim, and
                                                                      // that still never counts
        Assert.False(shape.Contains(new WorldVector(11.5d, 11.5d)));  // Euclidean: √(1.5²+1.5²) ≈ 2.12 > 2 (a
                                                                      // Chebyshev-2 corner is OUT of a radius-2 circle)
        Assert.Equal(2d, shape.BoundingRadius);
    }

    // HONEST-EDGE (T2 review followup 1): Schedule quantizes the shape to the SAME Q12.4 grid the wire ships, so the
    // server resolves EXACTLY the circle every client draws. The victim discriminates: its distance from the
    // QUANTIZED circle (origin 10.0625, radius 2.0625) is 2.0375 — inside — but from the RAW schedule args
    // (10.04, 2.04) it is 2.06 — outside. Resolving the unquantized shape fails this test.
    [Fact]
    public void ScheduleQuantizesShapeToWireGrid_ResolveAndWireSeeTheSameCircle()
    {
        var world = new WorldState();
        var victim = world.AddTransient(1, EntityKind.Player, "Rim", new TileCoord(12, 10), Direction8.S);
        MoveTo(world, victim, new WorldVector(12.1d, 10d));
        var (scheduler, _, landed) = CreateEngine(world, CreateExecutor(world));

        scheduler.Schedule(999, TelegraphShape.Circle(new WorldVector(10.04d, 10d), 2.04d), startTick: 10, resolveTick: 30, damage: 15, source: "quantize pin");

        // The wire projection carries the quantized shape — what the client draws IS what resolves below.
        var active = new List<TelegraphScheduler.ActiveTelegraph>();
        scheduler.CopyActiveTo(active);
        Assert.Equal(10.0625d, active[0].Shape.Origin.X, 6); // round(10.04·16 = 160.64) = 161 → 10.0625
        Assert.Equal(10d, active[0].Shape.Origin.Y, 6);      // on-grid axis unchanged
        Assert.Equal(2.0625d, active[0].Shape.Radius, 6);    // round(2.04·16 = 32.64) = 33 → 2.0625

        scheduler.ResolveDue(30);
        var hit = Assert.Single(landed);
        Assert.Same(victim, hit.Victim); // 12.1 − 10.0625 = 2.0375 ≤ 2.0625 — inside the QUANTIZED circle only
    }

    [Fact]
    public void ResolvesAtExactlyTickT_PlayerInsideAtT_IsHitOnce()
    {
        var world = new WorldState();
        var player = world.AddTransient(1, EntityKind.Player, "Hero", new TileCoord(32, 32), Direction8.S);
        var (scheduler, _, landed) = CreateEngine(world, CreateExecutor(world));

        // Scheduled at tick 20, resolving at tick 50 — the deadline form.
        scheduler.Schedule(999, TelegraphShape.Circle(player.Position, 2d), startTick: 20, resolveTick: 50, damage: 15, source: "test slam");
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
        scheduler.Schedule(999, TelegraphShape.Circle(origin, 2d), startTick: 20, resolveTick: 50, damage: 15, source: "test slam");
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
        scheduler.Schedule(999, TelegraphShape.Circle(origin, 2d), startTick: 20, resolveTick: 50, damage: 15, source: "test slam");
        MoveTo(world, player, origin + new WorldVector(0.5d, 0d));

        scheduler.ResolveDue(50);

        // Positions AT T, never at cast: walking in eats the hit even though the cast-time position was safe.
        Assert.Single(landed);
        Assert.Equal(85, player.Stats.Health);
    }

    [Fact]
    public void RimVictimBucketedInANeighboringGatherCell_FractionalOrigin_IsHit()
    {
        // T1-review followup (gather-margin rim pin): the resolve gather is a superset box around the LOCKED ORIGIN
        // (⌈BoundingRadius⌉ + 1 tiles), never around the caster. Every prior test parked victims ≤1 tile from the
        // origin inside the same spatial cell, so a regression that gathered around the CASTER's position instead of
        // the shape's origin passed the whole suite. Here the geometry stresses the gather edge: a FRACTIONAL origin
        // (32.49, 32), a victim whose center sits EXACTLY on the rim (34.49 − 32.49 is exactly 2.0 in doubles, so
        // LengthSquared == Radius² — the inclusive-rim hit), bucketed two tiles away in a NEIGHBORING spatial cell
        // (cell size 2: origin tile 32 → cell 16, victim tile 34 → cell 17), and a live caster parked FAR away
        // (10, 10) whose neighborhood does NOT contain the victim. Gathering around the caster — or any gather box
        // that fails to cover the rim of the origin's disc — misses this victim and fails the damage assert.
        var world = new WorldState(gridCellSize: 2);
        var caster = world.AddTransient(1, EntityKind.Monster, "Slime", new TileCoord(10, 10), Direction8.S);
        var player = world.AddTransient(2, EntityKind.Player, "Hero", new TileCoord(34, 32), Direction8.S);
        var (scheduler, _, landed) = CreateEngine(world, CreateExecutor(world));
        MoveTo(world, player, new WorldVector(34.49d, 32d));

        var origin = new WorldVector(32.49d, 32d);
        Assert.True(TelegraphShape.Circle(origin, 2d).Contains(player.Position),
            "test setup: the victim's center must sit exactly on the inclusive rim.");
        scheduler.Schedule(caster.Id, TelegraphShape.Circle(origin, 2d), startTick: 20, resolveTick: 50, damage: 15, source: "Slime slam");

        scheduler.ResolveDue(50);

        // "I was inside (on the rim) and it hit" — the origin-centered superset gather found the neighbor-cell victim.
        var hit = Assert.Single(landed);
        Assert.Same(player, hit.Victim);
        Assert.Equal(85, player.Stats.Health);
        Assert.Equal(0, scheduler.PendingCount);
    }

    [Fact]
    public void BodyClippingTheRim_CenterJustOutside_TakesNothing()
    {
        // CENTER-POINT membership — DECIDED (user, 2026-07-03; see TelegraphShape.Contains): you are hit iff your
        // CENTER is inside the drawn circle; a body clipping the rim never counts (deliberately divergent from the
        // melee/free-aim body-clip rule — the drawn circle IS the rule, ambiguity errs player-favorable). Victim
        // center at distance 2.2 from the origin of a radius-2 circle: OUTSIDE by 0.2, yet the 0.5-radius body
        // overlaps the rim by 0.3 — a body-clip rule (or any "helpful" body-radius padding in the resolve) would
        // damage this player; the decided rule must not.
        var world = new WorldState();
        var player = world.AddTransient(1, EntityKind.Player, "Hero", new TileCoord(34, 32), Direction8.S);
        var (scheduler, _, landed) = CreateEngine(world, CreateExecutor(world));
        MoveTo(world, player, new WorldVector(34.2d, 32d));

        var origin = new WorldVector(32d, 32d);
        Assert.True((player.Position - origin).Length - BodyRadius < 2d,
            "test setup: the body must overlap the drawn circle (only the center is outside).");
        scheduler.Schedule(999, TelegraphShape.Circle(origin, 2d), startTick: 20, resolveTick: 50, damage: 15, source: "test slam");

        scheduler.ResolveDue(50);

        // Center outside → no damage, even though the body clipped the rim. The telegraph still resolved (and left).
        Assert.Empty(landed);
        Assert.Equal(100, player.Stats.Health);
        Assert.Equal(0, scheduler.PendingCount);
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
        scheduler.Schedule(999, TelegraphShape.Circle(new WorldVector(32.5d, 32d), 3d), startTick: resolveTick - 30, resolveTick: resolveTick, damage: 15, source: "test slam");

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

        scheduler.Schedule(caster.Id, TelegraphShape.Circle(player.Position, 2d), startTick: 20, resolveTick: 50, damage: 15, source: "Slime slam");
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

        scheduler.Schedule(999, TelegraphShape.Circle(new WorldVector(32d, 32d), 2d), startTick: 20, resolveTick: 50, damage: 15, source: "test slam");
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
        scheduler.Schedule(999, shape, startTick: 10, resolveTick: 30, damage: 1, source: "a");
        scheduler.Schedule(999, shape, startTick: 10, resolveTick: 40, damage: 2, source: "b");
        scheduler.Schedule(999, shape, startTick: 10, resolveTick: 40, damage: 3, source: "c");
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

    // ================= TELEGRAPH SHAPES WEDGE+LINE (S-telegraph-shapes-wedge-line) =================

    [Fact]
    public void WedgeMembership_WithinReachAndArc_IsInclusive_ErrsPlayerFavorable()
    {
        // A 90° wedge (half-angle 45°) with APEX at (10,10), aimed east (+X, aim 0), reach 3.
        var half = 45d * Math.PI / 180d;
        var shape = TelegraphShape.Wedge(new WorldVector(10d, 10d), 3d, aimRadians: 0d, halfAngleRadians: half);

        Assert.True(shape.Contains(new WorldVector(10d, 10d)));       // the apex — inside
        Assert.True(shape.Contains(new WorldVector(12d, 10d)));       // straight ahead, in reach
        Assert.True(shape.Contains(new WorldVector(13d, 10d)));       // exactly on the reach rim (dist 3) — INCLUSIVE
        Assert.False(shape.Contains(new WorldVector(13.01d, 10d)));   // just past the reach
        Assert.False(shape.Contains(new WorldVector(8d, 10d)));       // directly BEHIND — outside the arc
        // On the arc edge (exactly 45° off the aim, within reach) — inclusive.
        Assert.True(shape.Contains(new WorldVector(10d + (2d * Math.Cos(half)), 10d + (2d * Math.Sin(half)))));
        // Just OUTSIDE the arc (47°): CENTER-POINT membership, player-favorable — no body widening (unlike free-aim).
        Assert.False(shape.Contains(new WorldVector(10d + (2d * Math.Cos(half + 0.035d)), 10d + (2d * Math.Sin(half + 0.035d)))));
        Assert.Equal(3d, shape.BoundingRadius, 9);
    }

    [Fact]
    public void LineMembership_WithinLengthAndWidth_IsInclusive_ErrsPlayerFavorable()
    {
        // A 2u-wide (half-width 1) corridor from origin (10,10), length 8, aimed east (+X).
        var shape = TelegraphShape.Line(new WorldVector(10d, 10d), length: 8d, aimRadians: 0d, halfWidth: 1d);

        Assert.True(shape.Contains(new WorldVector(10d, 10d)));       // the near edge (along 0)
        Assert.True(shape.Contains(new WorldVector(14d, 10.9d)));     // inside the corridor
        Assert.True(shape.Contains(new WorldVector(14d, 11d)));       // exactly on the side edge (perp 1) — INCLUSIVE
        Assert.False(shape.Contains(new WorldVector(14d, 11.01d)));   // just past the side edge
        Assert.True(shape.Contains(new WorldVector(18d, 10d)));       // exactly on the far edge (along 8) — INCLUSIVE
        Assert.False(shape.Contains(new WorldVector(18.01d, 10d)));   // just past the far edge
        Assert.False(shape.Contains(new WorldVector(9.99d, 10d)));    // BEHIND the near edge (along < 0)
        Assert.Equal(Math.Sqrt(64d + 1d), shape.BoundingRadius, 9);   // sqrt(length² + halfWidth²)
    }

    [Fact]
    public void WedgeResolve_PlayerInArc_IsHit_PlayerBehind_IsNot()
    {
        var world = new WorldState();
        var front = world.AddTransient(1, EntityKind.Player, "Front", new TileCoord(12, 10), Direction8.S);
        var behind = world.AddTransient(2, EntityKind.Player, "Behind", new TileCoord(8, 10), Direction8.S);
        var (scheduler, _, landed) = CreateEngine(world, CreateExecutor(world));

        var half = 45d * Math.PI / 180d;
        scheduler.Schedule(999, TelegraphShape.Wedge(new WorldVector(10d, 10d), 3d, 0d, half), startTick: 10, resolveTick: 20, damage: 25, source: "Cleave");
        scheduler.ResolveDue(20);

        var hit = Assert.Single(landed);
        Assert.Same(front, hit.Victim);
        Assert.Equal(75, front.Stats.Health);   // in-arc, in reach → hit
        Assert.Equal(100, behind.Stats.Health);  // behind the apex → outside the arc → untouched
    }

    [Fact]
    public void LineResolve_PlayerInCorridor_IsHit_PlayerBeside_IsNot()
    {
        var world = new WorldState();
        var inside = world.AddTransient(1, EntityKind.Player, "In", new TileCoord(14, 10), Direction8.S);
        var beside = world.AddTransient(2, EntityKind.Player, "Beside", new TileCoord(14, 13), Direction8.S);
        var (scheduler, _, landed) = CreateEngine(world, CreateExecutor(world));

        scheduler.Schedule(999, TelegraphShape.Line(new WorldVector(10d, 10d), 8d, 0d, 1d), startTick: 10, resolveTick: 20, damage: 20, source: "Lunge");
        scheduler.ResolveDue(20);

        var hit = Assert.Single(landed);
        Assert.Same(inside, hit.Victim);
        Assert.Equal(80, inside.Stats.Health);   // in the corridor → hit
        Assert.Equal(100, beside.Stats.Health);   // 3u off the centreline (half-width 1) → untouched
    }

    [Fact]
    public void ScheduleQuantizesWedgeAndLine_WireAndResolveSeeTheSameShape()
    {
        // The honest-telegraph pillar for the new kinds: the shape the scheduler holds (and resolves Contains against)
        // is the EXACT wire shape. Schedule OFF-GRID wedge + line; CopyActiveTo yields the quantized shape the wire
        // ships; encoding+decoding THAT shape is a FIXPOINT (survives the wire byte-identically) — so client decal ==
        // server resolve. QuantizeToWire (scheduler) and WriteTelegraphShape/ReadTelegraphShape (codec) must agree.
        var world = new WorldState();
        var (scheduler, _, _) = CreateEngine(world, CreateExecutor(world));
        scheduler.Schedule(1, TelegraphShape.Wedge(new WorldVector(10.04d, -3.03d), 2.77d, 1.1234d, 1.14d), 10, 20, 25, "Cleave");
        scheduler.Schedule(2, TelegraphShape.Line(new WorldVector(-1.53d, 4.71d), 7.96d, 2.41d, 0.97d), 10, 20, 20, "Lunge");

        var active = new List<TelegraphScheduler.ActiveTelegraph>();
        scheduler.CopyActiveTo(active);
        Assert.Equal(2, active.Count);
        foreach (var t in active)
        {
            var decoded = (TelegraphMessage)ProtocolCodec.Decode(
                ProtocolCodec.Encode(new TelegraphMessage(t.Id, t.Shape, t.StartTick, t.ResolveTick)));
            Assert.Equal(t.Shape, decoded.Shape);
        }
    }
}
