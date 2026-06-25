using Mmo.Server.Runtime;
using Mmo.Shared.Domain;
using Xunit;

namespace Mmo.Server.Tests;

// LIVING-ENEMIES P1/P2: headless tests for the monster brain (MonsterRoamAi), driven directly against a
// WorldState + TileGrid + injected target/attack callbacks (no network/GameServer). Randomness is seeded so every
// assertion is deterministic. These pin the behaviour the human live-verifies: a monster is still most of the time,
// occasionally strolls within its leash, AGGROS the nearest player in range, CHASES (leashed to home), ATTACKS when
// adjacent on its own cooldown, the player TAKES damage (HP floors at 0, no death), de-aggros when the target is
// lost / out of range / beyond the chase leash, and never freezes against a wall corner (the P1 corner-cut fix).
public sealed class MonsterRoamAiTests
{
    // A generous open grid so the leash is the only thing bounding the wander (no walls in the way) for the
    // leash/cadence tests. Walls are introduced explicitly in the blocked-target / corner-cut tests.
    private const int GridSize = 64;
    private const uint StepCooldownTicks = 3; // the project's base cadence (150 ms @ 20 Hz).

    private static TileGrid OpenGrid() => new(GridSize, GridSize, []);

    // The P1 roam-only tunables (no aggro: AggroRadius 0 disables the scan), so the existing leash/cadence tests
    // exercise pure roam behaviour. roamRadius/pause are passed per call where they vary.
    private static MonsterRoamAi.Tunables RoamTunables(int roamRadius, uint pauseMin, uint pauseMax)
        => new(
            RoamRadius: roamRadius,
            PauseMinTicks: pauseMin,
            PauseMaxTicks: pauseMax,
            AggroRadius: 0,            // disabled — these tests are roam-only.
            DeaggroRadius: 0,
            ChaseLeash: 0,
            AttackRange: 1,
            AttackDamage: 0,
            AttackCooldownTicks: 1,
            AggroScanIntervalTicks: 10);

    // Builds an AI whose walkability + stepper are wired to a real WorldState/TileGrid exactly like Zone does
    // (TryStep migrates the spatial bucket, so the entity's Tile advances on accept). The aggro/resolve/attack
    // callbacks default to "no aggro" so a roam-only test is unaffected; the P2 tests pass real ones.
    private static MonsterRoamAi CreateAi(
        int seed,
        TileGrid grid,
        WorldState world,
        MonsterRoamAi.FindTargetDelegate? findTarget = null,
        MonsterRoamAi.TryResolveTargetDelegate? tryResolve = null,
        MonsterRoamAi.AttackDelegate? attack = null)
    {
        return new MonsterRoamAi(
            seed,
            grid.IsWalkable,
            (entity, direction, tick, cooldownTicks) =>
            {
                var previous = entity.TileCoord;
                var stepped = entity.TryStep(direction, tick, cooldownTicks, grid, out _);
                if (stepped)
                {
                    world.OnEntityMoved(entity, previous);
                }

                return stepped;
            },
            findTarget ?? ((WorldEntity _, int _, out ulong id, out TileCoord tile) =>
            {
                id = 0;
                tile = default;
                return false;
            }),
            tryResolve ?? ((ulong _, out TileCoord tile, out bool alive) =>
            {
                tile = default;
                alive = false;
                return false;
            }),
            attack ?? ((WorldEntity _, ulong _, int _) => { }));
    }

    private static WorldEntity SpawnMonster(WorldState world, TileCoord tile, uint networkId = 1)
        => world.AddTransient(networkId, EntityKind.Monster, "Monster", tile, Direction8.S);

    [Fact]
    public void StaysWithinRoamRadiusOfHomeAcrossManyTicks()
    {
        // The core leash guarantee: across a long run the monster's tile is NEVER more than roamRadius (Chebyshev)
        // from its home anchor. Run a couple thousand ticks with a short pause so it roams often (a stress on the
        // leash), and assert the bound every tick.
        const int roamRadius = 4;
        var grid = OpenGrid();
        var world = new WorldState();
        var home = new TileCoord(32, 32);
        var monster = SpawnMonster(world, home);
        var ai = CreateAi(seed: 1234, grid, world);
        // Short pause (1-2 ticks) so it strolls frequently and we exercise the leash hard.
        ai.Register(monster, serverTick: 0, pauseMinTicks: 1, pauseMaxTicks: 2, aggroScanIntervalTicks: 10);

        for (uint tick = 1; tick <= 3000; tick++)
        {
            ai.StepMonster(monster, tick, StepCooldownTicks, RoamTunables(roamRadius, 1, 2));

            var dx = Math.Abs(monster.TileCoord.X - home.X);
            var dy = Math.Abs(monster.TileCoord.Y - home.Y);
            Assert.True(
                Math.Max(dx, dy) <= roamRadius,
                $"monster left the leash at tick {tick}: tile={monster.TileCoord}, home={home}, radius={roamRadius}");
        }
    }

    [Fact]
    public void DoesNotStepEveryTick_PausesBetweenStrolls()
    {
        // The "mostly still" feel: with a multi-second pause, the fraction of ticks on which the tile actually
        // advances must be well under 1 — the monster is idle far more than it walks. We count accepted advances
        // over a long run and assert they are a small minority of ticks.
        const int roamRadius = 4;
        var grid = OpenGrid();
        var world = new WorldState();
        var monster = SpawnMonster(world, new TileCoord(32, 32));
        var ai = CreateAi(seed: 77, grid, world);
        // ~2-5 s pauses at 20 Hz = 40-100 ticks idle between short strolls.
        const uint pauseMin = 40;
        const uint pauseMax = 100;
        ai.Register(monster, serverTick: 0, pauseMin, pauseMax, aggroScanIntervalTicks: 10);

        var advances = 0;
        const int ticks = 4000;
        for (uint tick = 1; tick <= ticks; tick++)
        {
            if (ai.StepMonster(monster, tick, StepCooldownTicks, RoamTunables(roamRadius, pauseMin, pauseMax)))
            {
                advances++;
            }
        }

        // It MUST move sometimes (it's a roamer, not a statue)...
        Assert.True(advances > 0, "monster never moved — it should occasionally stroll.");
        // ...but it must be idle the vast majority of the time. Even nonstop roaming within radius 4 at a 3-tick
        // cadence would advance at most ~1/3 of ticks; with 40-100 tick pauses between strolls the real fraction
        // is far lower. Assert a comfortable ceiling of 20% so the test is robust to the seed yet still proves
        // "mostly still".
        Assert.True(
            advances < ticks * 0.20,
            $"monster moved on {advances}/{ticks} ticks — expected mostly idle (< 20%).");
    }

    [Fact]
    public void TransitionsIdleToRoamingToIdleOnTheTimers()
    {
        // The state machine fires on the pause timer: starts Idle, stays Idle until the pause elapses, then enters
        // Roaming when it picks a destination, and returns to Idle once it arrives.
        const int roamRadius = 4;
        var grid = OpenGrid();
        var world = new WorldState();
        var monster = SpawnMonster(world, new TileCoord(32, 32));
        var ai = CreateAi(seed: 5, grid, world);
        const uint pause = 10; // fixed pause so the transition tick is predictable.
        ai.Register(monster, serverTick: 0, pauseMinTicks: pause, pauseMaxTicks: pause, aggroScanIntervalTicks: 10);

        // Before the pause elapses it stays Idle and does not move.
        for (uint tick = 1; tick < pause; tick++)
        {
            ai.StepMonster(monster, tick, StepCooldownTicks, RoamTunables(roamRadius, pause, pause));
            Assert.True(ai.TryGetPhase(monster.Id, out var phase) && phase == MonsterRoamAi.State.Idle,
                $"expected Idle before pause elapses (tick {tick}).");
        }
        var homeTile = monster.TileCoord;
        Assert.Equal(new TileCoord(32, 32), homeTile);

        // At/after the pause it picks a destination and starts Roaming (the open grid always has an open target).
        ai.StepMonster(monster, pause, StepCooldownTicks, RoamTunables(roamRadius, pause, pause));
        Assert.True(ai.TryGetPhase(monster.Id, out var roamingPhase));
        Assert.Equal(MonsterRoamAi.State.Roaming, roamingPhase);

        // Drive it long enough to reach the destination and flip back to Idle at least once.
        var sawIdleAgain = false;
        for (uint tick = pause + 1; tick <= pause + 200; tick++)
        {
            ai.StepMonster(monster, tick, StepCooldownTicks, RoamTunables(roamRadius, pause, pause));
            if (ai.TryGetPhase(monster.Id, out var p) && p == MonsterRoamAi.State.Idle)
            {
                sawIdleAgain = true;
                break;
            }
        }

        Assert.True(sawIdleAgain, "monster never returned to Idle after roaming.");
        // And it actually moved away from home at some point during the roam (it strolled).
        // (We can't assert the exact tile — it's seeded random — but a roam target != home, and a greedy walk
        // toward it means the tile changed.)
    }

    [Fact]
    public void RoamTargetIsAlwaysOpenAndReachable_WalkedToWithoutLeavingLeash()
    {
        // Every roam target the AI picks must be an OPEN tile within the leash, and a greedy walk reaches it. We
        // verify indirectly but strongly: over a long run on a grid with some interior walls inside the leash, the
        // monster never ends a step on a blocked tile and never leaves the leash — i.e. it only ever walks onto
        // open, in-radius tiles. A blocked target would either strand it on a wall (impossible — TryStep rejects)
        // or wedge it; the leash + walkability assertions catch both.
        const int roamRadius = 5;
        var home = new TileCoord(32, 32);
        // Scatter a few walls INSIDE the leash box so destination-picking must avoid them.
        var walls = new[]
        {
            new TileCoord(33, 32), new TileCoord(32, 33), new TileCoord(34, 30),
            new TileCoord(30, 34), new TileCoord(35, 35), new TileCoord(29, 31),
        };
        var grid = new TileGrid(GridSize, GridSize, walls);
        var world = new WorldState();
        var monster = SpawnMonster(world, home);
        var ai = CreateAi(seed: 999, grid, world);
        ai.Register(monster, serverTick: 0, pauseMinTicks: 1, pauseMaxTicks: 3, aggroScanIntervalTicks: 10);

        for (uint tick = 1; tick <= 4000; tick++)
        {
            ai.StepMonster(monster, tick, StepCooldownTicks, RoamTunables(roamRadius, 1, 3));

            // Never standing on a wall.
            Assert.True(grid.IsWalkable(monster.TileCoord), $"monster on a blocked tile {monster.TileCoord} at tick {tick}.");
            // Never outside the leash.
            var dx = Math.Abs(monster.TileCoord.X - home.X);
            var dy = Math.Abs(monster.TileCoord.Y - home.Y);
            Assert.True(Math.Max(dx, dy) <= roamRadius, $"monster left the leash at tick {tick}: {monster.TileCoord}.");
        }
    }

    [Fact]
    public void IsDeterministicForAGivenSeed()
    {
        // Reproducibility: two AIs with the same seed and identical inputs produce the identical roam path. This is
        // what lets the headless tests be deterministic and a live repro be replayable.
        var path1 = RunSeededPath(seed: 42);
        var path2 = RunSeededPath(seed: 42);
        Assert.Equal(path1, path2);

        // And a different seed diverges (so the seed actually drives the randomness, not a fixed pattern).
        var path3 = RunSeededPath(seed: 43);
        Assert.NotEqual(path1, path3);
    }

    private static List<TileCoord> RunSeededPath(int seed)
    {
        const int roamRadius = 4;
        var grid = OpenGrid();
        var world = new WorldState();
        var monster = SpawnMonster(world, new TileCoord(32, 32));
        var ai = CreateAi(seed, grid, world);
        ai.Register(monster, serverTick: 0, pauseMinTicks: 2, pauseMaxTicks: 4, aggroScanIntervalTicks: 10);

        var path = new List<TileCoord>();
        for (uint tick = 1; tick <= 500; tick++)
        {
            ai.StepMonster(monster, tick, StepCooldownTicks, RoamTunables(roamRadius, 2, 4));
            path.Add(monster.TileCoord);
        }

        return path;
    }

    [Fact]
    public void HasFreshlyRegisteredHomeAnchor()
    {
        var grid = OpenGrid();
        var world = new WorldState();
        var home = new TileCoord(20, 25);
        var monster = SpawnMonster(world, home);
        var ai = CreateAi(seed: 1, grid, world);
        ai.Register(monster, serverTick: 0, pauseMinTicks: 2, pauseMaxTicks: 4, aggroScanIntervalTicks: 10);

        Assert.Equal(1, ai.TrackedCount);
        Assert.True(ai.TryGetHome(monster.Id, out var stored));
        Assert.Equal(home, stored);
        Assert.True(ai.TryGetPhase(monster.Id, out var phase) && phase == MonsterRoamAi.State.Idle);
    }

    // ---------------------------------------------------------------------------------------------------------
    // LIVING-ENEMIES P2: aggro / chase / attack / de-aggro / corner-cut.
    // ---------------------------------------------------------------------------------------------------------

    // A full aggro-enabled tunable set with sensible defaults; individual values overridable per test.
    private static MonsterRoamAi.Tunables CombatTunables(
        int roamRadius = 4,
        uint pauseMin = 100,
        uint pauseMax = 100,
        int aggroRadius = 6,
        int deaggroRadius = 9,
        int chaseLeash = 12,
        int attackRange = 1,
        int attackDamage = 10,
        uint attackCooldownTicks = 20,
        uint aggroScanInterval = 1)
        => new(
            roamRadius, pauseMin, pauseMax,
            aggroRadius, deaggroRadius, chaseLeash,
            attackRange, attackDamage, attackCooldownTicks, aggroScanInterval);

    // Wires the AI's aggro/resolve/attack callbacks to a real player WorldEntity in the world, mirroring GameServer:
    //   findTarget — nearest alive player within aggroRadius (Chebyshev).
    //   tryResolve — the target's live tile + alive (Health > 0); false if the entity is removed from the world.
    //   attack     — face + ApplyDamage; `hitCounter` counts real hits (HP actually changed).
    private static MonsterRoamAi CreateCombatAi(
        int seed, TileGrid grid, WorldState world, WorldEntity player, int[] hitCounter)
    {
        return CreateAi(
            seed, grid, world,
            findTarget: (WorldEntity monster, int aggroRadius, out ulong id, out TileCoord tile) =>
            {
                if (!world.TryGet(player.Id, out var p) || p.Stats.Health <= 0)
                {
                    id = 0;
                    tile = default;
                    return false;
                }

                var dist = Math.Max(Math.Abs(p.TileCoord.X - monster.TileCoord.X), Math.Abs(p.TileCoord.Y - monster.TileCoord.Y));
                if (dist > aggroRadius)
                {
                    id = 0;
                    tile = default;
                    return false;
                }

                id = p.Id;
                tile = p.TileCoord;
                return true;
            },
            tryResolve: (ulong id, out TileCoord tile, out bool alive) =>
            {
                if (world.TryGet(id, out var e))
                {
                    tile = e.TileCoord;
                    alive = e.Stats.Health > 0;
                    return true;
                }

                tile = default;
                alive = false;
                return false;
            },
            attack: (WorldEntity monster, ulong id, int damage) =>
            {
                if (world.TryGet(id, out var e) && e.ApplyDamage(damage))
                {
                    hitCounter[0]++;
                }
            });
    }

    [Fact]
    public void AggrosWhenAPlayerEntersRange()
    {
        // A player two tiles from an idle monster, well within the aggro radius, makes the monster enter Chasing on
        // the first scan and target that player.
        var grid = OpenGrid();
        var world = new WorldState();
        var monster = SpawnMonster(world, new TileCoord(32, 32), networkId: 1);
        var player = world.AddTransient(2, EntityKind.Player, "Hero", new TileCoord(34, 32), Direction8.S);
        var hits = new int[1];
        var ai = CreateCombatAi(seed: 7, grid, world, player, hits);
        ai.Register(monster, serverTick: 0, pauseMinTicks: 100, pauseMaxTicks: 100, aggroScanIntervalTicks: 1);

        ai.StepMonster(monster, serverTick: 1, StepCooldownTicks, CombatTunables());

        Assert.True(ai.TryGetPhase(monster.Id, out var phase));
        Assert.Equal(MonsterRoamAi.State.Chasing, phase);
        Assert.True(ai.TryGetTarget(monster.Id, out var targetId));
        Assert.Equal(player.Id, targetId);
    }

    [Fact]
    public void DoesNotAggroAPlayerOutOfRange()
    {
        // A player FAR outside the aggro radius is never acquired; the monster stays in its roam state machine.
        var grid = OpenGrid();
        var world = new WorldState();
        var monster = SpawnMonster(world, new TileCoord(32, 32), networkId: 1);
        var player = world.AddTransient(2, EntityKind.Player, "Hero", new TileCoord(50, 50), Direction8.S);
        var hits = new int[1];
        var ai = CreateCombatAi(seed: 7, grid, world, player, hits);
        ai.Register(monster, serverTick: 0, pauseMinTicks: 100, pauseMaxTicks: 100, aggroScanIntervalTicks: 1);

        for (uint tick = 1; tick <= 50; tick++)
        {
            ai.StepMonster(monster, tick, StepCooldownTicks, CombatTunables());
            Assert.True(ai.TryGetPhase(monster.Id, out var phase));
            Assert.NotEqual(MonsterRoamAi.State.Chasing, phase);
        }
    }

    [Fact]
    public void ChaseStepsReduceDistanceToTarget()
    {
        // A monster aggros a player several tiles away and greedily closes the gap: the Chebyshev distance to the
        // target strictly decreases over the chase until it is adjacent (then it attacks instead of moving).
        var grid = OpenGrid();
        var world = new WorldState();
        var monster = SpawnMonster(world, new TileCoord(32, 32), networkId: 1);
        var player = world.AddTransient(2, EntityKind.Player, "Hero", new TileCoord(38, 32), Direction8.S);
        var hits = new int[1];
        var ai = CreateCombatAi(seed: 7, grid, world, player, hits);
        ai.Register(monster, serverTick: 0, pauseMinTicks: 100, pauseMaxTicks: 100, aggroScanIntervalTicks: 1);

        var startDist = Chebyshev(monster.TileCoord, player.TileCoord);
        var minDist = startDist;
        for (uint tick = 1; tick <= 60; tick++)
        {
            ai.StepMonster(monster, tick, StepCooldownTicks, CombatTunables());
            minDist = Math.Min(minDist, Chebyshev(monster.TileCoord, player.TileCoord));
        }

        // It closed to adjacency (attackRange 1).
        Assert.True(minDist <= 1, $"monster never closed to the player (min Chebyshev {minDist}).");
        Assert.True(minDist < startDist, "monster did not move closer at all.");
    }

    [Fact]
    public void AttacksWhenAdjacentRespectsCooldownAndDealsDamage()
    {
        // A monster adjacent to a stationary player lands hits ONLY on its own attack cooldown, each dealing exactly
        // attackDamage. Over N ticks at cooldown C, the number of hits is ~N/C, not one per tick.
        const int attackDamage = 10;
        const uint cooldown = 20; // 1 s @ 20 Hz.
        var grid = OpenGrid();
        var world = new WorldState();
        var monster = SpawnMonster(world, new TileCoord(32, 32), networkId: 1);
        var player = world.AddTransient(2, EntityKind.Player, "Hero", new TileCoord(33, 32), Direction8.S);
        var startHp = player.Stats.Health;
        var hits = new int[1];
        var ai = CreateCombatAi(seed: 7, grid, world, player, hits);
        ai.Register(monster, serverTick: 0, pauseMinTicks: 100, pauseMaxTicks: 100, aggroScanIntervalTicks: 1);

        // 100 ticks adjacent. With cooldown 20 and the first hit eligible immediately (NextAttackTick seeded to
        // spawn tick), expect hits at ticks 1, 21, 41, 61, 81 → 5 hits.
        const int ticks = 100;
        for (uint tick = 1; tick <= ticks; tick++)
        {
            ai.StepMonster(monster, tick, StepCooldownTicks, CombatTunables(attackDamage: attackDamage, attackCooldownTicks: cooldown));
        }

        Assert.Equal(5, hits[0]);
        Assert.Equal(startHp - 5 * attackDamage, world.TryGet(player.Id, out var p) ? p.Stats.Health : -1);
        // The monster faces the player (east, since the player is at +1 X).
        Assert.Equal(Direction8.E, monster.Facing);
    }

    [Fact]
    public void DeaggrosWhenTargetIsRemovedAndReturnsHome()
    {
        // Target lost (despawn / logout): the monster drops aggro, walks back toward home, and resumes Idle on arrival.
        var grid = OpenGrid();
        var world = new WorldState();
        var home = new TileCoord(32, 32);
        var monster = SpawnMonster(world, home, networkId: 1);
        var player = world.AddTransient(2, EntityKind.Player, "Hero", new TileCoord(36, 32), Direction8.S);
        var hits = new int[1];
        var ai = CreateCombatAi(seed: 7, grid, world, player, hits);
        ai.Register(monster, serverTick: 0, pauseMinTicks: 5, pauseMaxTicks: 5, aggroScanIntervalTicks: 1);

        // Aggro + chase a few tiles away from home.
        for (uint tick = 1; tick <= 12; tick++)
        {
            ai.StepMonster(monster, tick, StepCooldownTicks, CombatTunables(pauseMin: 5, pauseMax: 5));
        }

        Assert.True(ai.TryGetPhase(monster.Id, out var chasing) && chasing == MonsterRoamAi.State.Chasing);
        Assert.True(Chebyshev(monster.TileCoord, home) > 0, "monster should have left home while chasing.");

        // Remove the target. Next tick the monster must drop aggro (Returning), then walk home and resume Idle.
        world.Remove(player.Id, out _);
        var resumed = false;
        for (uint tick = 13; tick <= 200; tick++)
        {
            ai.StepMonster(monster, tick, StepCooldownTicks, CombatTunables(pauseMin: 5, pauseMax: 5));
            Assert.True(ai.TryGetPhase(monster.Id, out var phase));
            Assert.NotEqual(MonsterRoamAi.State.Chasing, phase); // never re-chases a gone target.
            if (monster.TileCoord == home && phase == MonsterRoamAi.State.Idle)
            {
                resumed = true;
                break;
            }
        }

        Assert.True(resumed, "monster never returned home + resumed Idle after de-aggro.");
    }

    [Fact]
    public void DeaggrosWhenTargetLeavesDeaggroRange()
    {
        // The player walks far beyond the de-aggro range: the monster drops the chase and returns home.
        var grid = OpenGrid();
        var world = new WorldState();
        var home = new TileCoord(32, 32);
        var monster = SpawnMonster(world, home, networkId: 1);
        var player = world.AddTransient(2, EntityKind.Player, "Hero", new TileCoord(35, 32), Direction8.S);
        var hits = new int[1];
        var ai = CreateCombatAi(seed: 7, grid, world, player, hits);
        ai.Register(monster, serverTick: 0, pauseMinTicks: 5, pauseMaxTicks: 5, aggroScanIntervalTicks: 1);

        // Aggro.
        ai.StepMonster(monster, serverTick: 1, StepCooldownTicks, CombatTunables(pauseMin: 5, pauseMax: 5));
        Assert.True(ai.TryGetPhase(monster.Id, out var chasing) && chasing == MonsterRoamAi.State.Chasing);

        // Walk the SAME player far east, past the de-aggro range (9) — the resolver re-reads its CURRENT tile each
        // step, so this exercises the de-aggro-RANGE branch (not target-lost). Step at cooldown 1 with spaced ticks.
        for (var i = 0; i < 25; i++)
        {
            var before = player.TileCoord;
            if (player.TryStep(Direction8.E, serverTick: (uint)(100 + i * 2), stepCooldownTicks: 1, grid, out _))
            {
                world.OnEntityMoved(player, before);
            }
        }

        Assert.True(Chebyshev(monster.TileCoord, player.TileCoord) > 12, "test setup: player not far enough to break leash.");

        var returned = false;
        for (uint tick = 2; tick <= 200; tick++)
        {
            ai.StepMonster(monster, tick, StepCooldownTicks, CombatTunables(pauseMin: 5, pauseMax: 5));
            if (ai.TryGetPhase(monster.Id, out var phase) && monster.TileCoord == home && phase == MonsterRoamAi.State.Idle)
            {
                returned = true;
                break;
            }
        }

        Assert.True(returned, "monster did not return home after the target left de-aggro range.");
    }

    [Fact]
    public void DeaggrosWhenPulledBeyondChaseLeash()
    {
        // A persistent player who keeps just within de-aggro range but drags the monster past chaseLeash tiles from
        // home makes the monster give up (the home-leash bound, distinct from the de-aggro-range bound).
        var grid = OpenGrid();
        var world = new WorldState();
        var home = new TileCoord(20, 20);
        var monster = SpawnMonster(world, home, networkId: 1);
        // Player sits just inside de-aggro range but far from the monster's home, so chasing it pulls the monster
        // past the chaseLeash. chaseLeash 5 (small) so the leash trips before the monster reaches the player.
        var player = world.AddTransient(2, EntityKind.Player, "Hero", new TileCoord(40, 20), Direction8.S);
        var hits = new int[1];
        var ai = CreateCombatAi(seed: 7, grid, world, player, hits);
        ai.Register(monster, serverTick: 0, pauseMinTicks: 5, pauseMaxTicks: 5, aggroScanIntervalTicks: 1);

        // Big aggro + de-aggro radius so it WOULD chase forever if not for the home leash; small chaseLeash (5).
        MonsterRoamAi.Tunables T() => CombatTunables(
            pauseMin: 5, pauseMax: 5, aggroRadius: 64, deaggroRadius: 96, chaseLeash: 5);

        var returnedHome = false;
        for (uint tick = 1; tick <= 400; tick++)
        {
            ai.StepMonster(monster, tick, StepCooldownTicks, T());
            // The monster is bounded by the chase leash: it may reach chaseLeash (and step one tile past before the
            // next tick's leash check trips Returning), but never wanders far — assert a tight bound of leash+1.
            Assert.True(Chebyshev(monster.TileCoord, home) <= 6,
                $"monster exceeded the chase leash at tick {tick}: {monster.TileCoord}, home {home}.");
            if (ai.TryGetPhase(monster.Id, out var phase) && monster.TileCoord == home && phase == MonsterRoamAi.State.Idle)
            {
                returnedHome = true;
                break;
            }
        }

        Assert.True(returnedHome, "monster never gave up + returned home under the chase leash.");
    }

    [Fact]
    public void PlayerHpFloorsAtZeroAndDoesNotDie()
    {
        // The player's HP floors at 0 under sustained attacks; the monster keeps attacking a 0-HP target (the attack
        // callback no-ops once HP is 0), and the player is never removed (no death/respawn this phase).
        var grid = OpenGrid();
        var world = new WorldState();
        var monster = SpawnMonster(world, new TileCoord(32, 32), networkId: 1);
        var player = world.AddTransient(2, EntityKind.Player, "Hero", new TileCoord(33, 32), Direction8.S);
        var hits = new int[1];
        var ai = CreateCombatAi(seed: 7, grid, world, player, hits);
        ai.Register(monster, serverTick: 0, pauseMinTicks: 100, pauseMaxTicks: 100, aggroScanIntervalTicks: 1);

        // Huge damage + a 1-tick cooldown so HP empties fast; run long past zero.
        for (uint tick = 1; tick <= 500; tick++)
        {
            ai.StepMonster(monster, tick, StepCooldownTicks, CombatTunables(attackDamage: 9999, attackCooldownTicks: 1));
        }

        Assert.True(world.TryGet(player.Id, out var p), "player must still exist (no death/respawn).");
        Assert.Equal(0, p.Stats.Health); // floored at 0, not negative.
    }

    [Fact]
    public void ChaseDoesNotFreezeOnACornerCutDiagonal()
    {
        // The P1 corner-cut livelock, exercised through a CHASE: the monster's only greedy route toward the target
        // is a diagonal that cuts a wall corner (TryStep rejects it, but the destination tile itself is walkable, so
        // the terrain-only check would think it's a cooldown wait and spin forever). The no-progress watchdog must
        // bail it out (to Returning) so it does not freeze. We assert it changes phase away from a frozen chase.
        //
        // Layout: monster at (10,10). Walls at (11,10) and (10,11) box the SE corner so the greedy step toward a
        // target at (12,12) is SE — a corner-cut. The monster cannot make progress toward the target.
        var walls = new[] { new TileCoord(11, 10), new TileCoord(10, 11) };
        var grid = new TileGrid(GridSize, GridSize, walls);
        var world = new WorldState();
        var home = new TileCoord(10, 10);
        var monster = SpawnMonster(world, home, networkId: 1);
        var player = world.AddTransient(2, EntityKind.Player, "Hero", new TileCoord(12, 12), Direction8.S);
        var hits = new int[1];
        var ai = CreateCombatAi(seed: 7, grid, world, player, hits);
        ai.Register(monster, serverTick: 0, pauseMinTicks: 5, pauseMaxTicks: 5, aggroScanIntervalTicks: 1);

        // Aggro on tick 1.
        ai.StepMonster(monster, serverTick: 1, StepCooldownTicks, CombatTunables(pauseMin: 5, pauseMax: 5));
        Assert.True(ai.TryGetPhase(monster.Id, out var chasing) && chasing == MonsterRoamAi.State.Chasing);

        // Drive it: it is wedged at the corner (can't step SE). The watchdog (≈ 2 step windows + margin) must bail it
        // off the chase rather than spinning forever on the same rejected diagonal. Within a bounded number of ticks
        // it must leave Chasing (to Returning/Idle) — i.e. it did NOT freeze.
        var bailed = false;
        for (uint tick = 2; tick <= 60; tick++)
        {
            ai.StepMonster(monster, tick, StepCooldownTicks, CombatTunables(pauseMin: 5, pauseMax: 5));
            if (ai.TryGetPhase(monster.Id, out var phase) && phase != MonsterRoamAi.State.Chasing)
            {
                bailed = true;
                break;
            }
        }

        Assert.True(bailed, "monster froze on a corner-cut diagonal during chase (no-progress watchdog failed).");
        // And it never wedged onto a wall.
        Assert.True(grid.IsWalkable(monster.TileCoord), $"monster on a blocked tile {monster.TileCoord}.");
    }

    [Fact]
    public void RoamDoesNotFreezeOnACornerCutDiagonal()
    {
        // The original P1 follow-up case (todo/monster-roam-cornercut-livelock.md): a ROAMING monster whose only
        // greedy route to its destination is a corner-cut diagonal must NOT spin forever — the no-progress watchdog
        // bails it back to Idle. We box the monster so a diagonal roam target requires a corner-cut, then assert it
        // returns to Idle (does not stay Roaming wedged) within the timeout and never stands on a wall.
        //
        // Monster at (10,10); walls at (11,10) and (10,11) make any SE roam target a corner-cut. With a tiny leash
        // and short pause it picks targets often; whenever it picks an SE-ish one it would wedge — the watchdog must
        // recover it. Over a long run it must never end a step on a wall and must keep cycling (not stay frozen).
        var walls = new[] { new TileCoord(11, 10), new TileCoord(10, 11) };
        var grid = new TileGrid(GridSize, GridSize, walls);
        var world = new WorldState();
        var monster = SpawnMonster(world, new TileCoord(10, 10), networkId: 1);
        // seed chosen so the destination picker hands it an SE corner-cut target early (deterministic).
        var ai = CreateAi(seed: 3, grid, world);
        ai.Register(monster, serverTick: 0, pauseMinTicks: 2, pauseMaxTicks: 2, aggroScanIntervalTicks: 10);

        var sawIdleAfterWedge = false;
        for (uint tick = 1; tick <= 2000; tick++)
        {
            ai.StepMonster(monster, tick, StepCooldownTicks, RoamTunables(2, 2, 2));
            Assert.True(grid.IsWalkable(monster.TileCoord), $"monster wedged on a wall {monster.TileCoord} at tick {tick}.");
            // It must keep returning to Idle (the cycle never permanently sticks in Roaming).
            if (tick > 50 && ai.TryGetPhase(monster.Id, out var phase) && phase == MonsterRoamAi.State.Idle)
            {
                sawIdleAfterWedge = true;
            }
        }

        Assert.True(sawIdleAfterWedge, "roaming monster appears frozen (never observed Idle after the early ticks).");
    }

    private static int Chebyshev(TileCoord a, TileCoord b)
        => Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
}
