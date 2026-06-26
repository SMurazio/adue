using Mmo.Server.Runtime;
using Mmo.Shared.Domain;
using Xunit;

namespace Mmo.Server.Tests;

// LIVING-ENEMIES P1/P2 + CONTINUOUS MIGRATION (Phase 8): headless tests for the monster brain (MonsterRoamAi), driven
// directly against a WorldState + TileGrid + the real HopLocomotion + injected target/attack callbacks (no
// network/GameServer). Randomness is seeded so every assertion is deterministic. These pin the CONTINUOUS-NAVIGATION /
// HOP contract the human live-verifies: a monster is still most of the time, occasionally HOPS within its Euclidean
// leash, AGGROS the nearest player in the Euclidean aggro disc, CHASES (Euclidean-leashed to home), ATTACKS when
// within AttackRangeUnits on its own cooldown, de-aggros at the Euclidean de-aggro/leash ranges, NEVER lands a hop
// inside a wall (the collision-valid headline), SOME hops land sub-tile (proving continuous nav), and never freezes
// against a wall the resolver slides to a fixpoint (the re-based livelock watchdog).
public sealed class MonsterRoamAiTests
{
    // A generous open grid so the leash is the only thing bounding the wander for the leash/cadence tests. Walls are
    // introduced explicitly in the collision-valid / wedge tests.
    private const int GridSize = 64;
    private const uint StepCooldownTicks = 3;        // the project's base cadence.
    private const double BodyRadius = 0.5d;          // the player body radius the monster also collides at.
    private const double HopDistance = 1.0d;         // one tile per hop (the default slime knob).

    private static TileGrid OpenGrid() => new(GridSize, GridSize, []);

    // Roam-only tunables (no aggro: AggroRadius 0 disables the scan). Euclidean float ranges.
    private static MonsterRoamAi.Tunables RoamTunables(double roamRadius, uint pauseMin, uint pauseMax)
        => new(
            RoamRadius: roamRadius,
            PauseMinTicks: pauseMin,
            PauseMaxTicks: pauseMax,
            AggroRadius: 0d,            // disabled — these tests are roam-only.
            DeaggroRadius: 0d,
            ChaseLeash: 0d,
            AttackRangeUnits: 1.5d,
            AttackDamage: 0,
            AttackCooldownTicks: 1,
            AggroScanIntervalTicks: 10);

    // Builds an AI whose walkability oracle + REAL HopLocomotion are wired to a TileGrid exactly like GameServer does
    // (the locomotion queries the same shared TileWalls + collides at the same body radius players do). The
    // aggro/resolve/attack callbacks default to "no aggro" so a roam-only test is unaffected; the combat tests pass real ones.
    private static MonsterRoamAi CreateAi(
        int seed,
        TileGrid grid,
        WorldState world,
        MonsterRoamAi.FindTargetDelegate? findTarget = null,
        MonsterRoamAi.TryResolveTargetDelegate? tryResolve = null,
        MonsterRoamAi.AttackDelegate? attack = null)
    {
        var locomotion = new HopLocomotion(
            () => HopDistance,
            () => BodyRadius,
            grid.QueryNearbyWalls,
            (entity, landing) =>
            {
                // Apply the landing + migrate the spatial bucket on a tile cross, exactly like Zone.ApplyMonsterLanding.
                var previous = entity.TileCoord;
                var crossed = entity.ApplyResolvedMove(landing);
                if (crossed)
                {
                    world.OnEntityMoved(entity, previous);
                }

                return crossed;
            });
        return new MonsterRoamAi(
            seed,
            grid.IsWalkable,
            locomotion,
            findTarget ?? ((WorldEntity _, int _, out ulong id, out WorldVector pos) =>
            {
                id = 0;
                pos = default;
                return false;
            }),
            tryResolve ?? ((ulong _, out WorldVector pos, out bool alive) =>
            {
                pos = default;
                alive = false;
                return false;
            }),
            attack ?? ((WorldEntity _, ulong _, int _) => { }));
    }

    private static WorldEntity SpawnMonster(WorldState world, TileCoord tile, uint networkId = 1)
        => world.AddTransient(networkId, EntityKind.Monster, "Monster", tile, Direction8.S);

    private static double Distance(WorldVector a, WorldVector b) => (a - b).Length;

    // The 8 tiles surrounding `centre` (cardinals + corners) — a TRUE enclosure for a body radius 0.5 (a four-cardinal
    // box leaks diagonally through an open corner, which the resolver legitimately rounds). Used by the wedge tests.
    private static TileCoord[] EnclosingWalls(TileCoord centre)
    {
        var walls = new List<TileCoord>();
        for (var dy = -1; dy <= 1; dy++)
        {
            for (var dx = -1; dx <= 1; dx++)
            {
                if (dx != 0 || dy != 0)
                {
                    walls.Add(new TileCoord(centre.X + dx, centre.Y + dy));
                }
            }
        }

        return walls.ToArray();
    }

    // Assert the body CENTRE (Position) is collision-valid: at least `radius` (minus a tiny float tolerance) from every
    // blocked tile's 1x1 AABB. This is the resolver's invariant — a hop must never land the circle penetrating a wall.
    private static void AssertCollisionValid(WorldVector pos, IReadOnlySet<TileCoord> blocked, string where)
    {
        const double tol = 1e-6;
        foreach (var tile in blocked)
        {
            // Closest point on the tile AABB [tx-0.5,ty-0.5 .. tx+0.5,ty+0.5] to the centre.
            var cx = Math.Clamp(pos.X, tile.X - 0.5d, tile.X + 0.5d);
            var cy = Math.Clamp(pos.Y, tile.Y - 0.5d, tile.Y + 0.5d);
            var d = Distance(pos, new WorldVector(cx, cy));
            Assert.True(d >= BodyRadius - tol,
                $"{where}: body centre {pos} penetrates blocked tile {tile} (dist {d:F4} < radius {BodyRadius}).");
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // Roam / leash / cadence / sub-tile / determinism.
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void StaysWithinEuclideanRoamRadiusOfHomeAcrossManyTicks()
    {
        // The core leash guarantee, now Euclidean: across a long run the monster's Position is never more than
        // roamRadius (Euclidean) + one hop of overshoot tolerance from its home. A roam target is sampled inside the
        // disc, so a hop toward it can overshoot by at most one HopDistance before the next pass re-pulls it in.
        const double roamRadius = 4d;
        var grid = OpenGrid();
        var world = new WorldState();
        var home = WorldVector.FromTile(new TileCoord(32, 32));
        var monster = SpawnMonster(world, home.ToTileRounded());
        var ai = CreateAi(seed: 1234, grid, world);
        ai.Register(monster, serverTick: 0, pauseMinTicks: 1, pauseMaxTicks: 2, aggroScanIntervalTicks: 10);

        for (uint tick = 1; tick <= 3000; tick++)
        {
            ai.StepMonster(monster, tick, StepCooldownTicks, RoamTunables(roamRadius, 1, 2));
            Assert.True(
                Distance(monster.Position, home) <= roamRadius + HopDistance + 1e-6,
                $"monster left the Euclidean leash at tick {tick}: pos={monster.Position}, home={home}.");
        }
    }

    [Fact]
    public void DoesNotHopEveryTick_PausesBetweenStrolls()
    {
        // The "mostly still" feel: with a multi-second pause, the fraction of ticks on which the monster actually hops
        // is well under 1 — it is idle far more than it moves.
        const double roamRadius = 4d;
        var grid = OpenGrid();
        var world = new WorldState();
        var monster = SpawnMonster(world, new TileCoord(32, 32));
        var ai = CreateAi(seed: 77, grid, world);
        const uint pauseMin = 40;
        const uint pauseMax = 100;
        ai.Register(monster, serverTick: 0, pauseMin, pauseMax, aggroScanIntervalTicks: 10);

        var hops = 0;
        const int ticks = 4000;
        for (uint tick = 1; tick <= ticks; tick++)
        {
            if (ai.StepMonster(monster, tick, StepCooldownTicks, RoamTunables(roamRadius, pauseMin, pauseMax)))
            {
                hops++;
            }
        }

        Assert.True(hops > 0, "monster never hopped — it should occasionally stroll.");
        Assert.True(hops < ticks * 0.20, $"monster hopped on {hops}/{ticks} ticks — expected mostly idle (< 20%).");
    }

    [Fact]
    public void HopsLandCollisionValid_AndSomeLandSubTile()
    {
        // THE HEADLINE: every hop lands collision-valid (the body centre never penetrates a blocked tile within the
        // body radius), AND — proving continuous nav — at least some hops land SUB-TILE (Position != its rounded tile
        // centre). A scatter of interior walls forces real slides + sub-tile landings.
        const double roamRadius = 6d;
        var home = new TileCoord(32, 32);
        var walls = new[]
        {
            new TileCoord(34, 32), new TileCoord(32, 34), new TileCoord(35, 30),
            new TileCoord(30, 35), new TileCoord(36, 36), new TileCoord(29, 31),
            new TileCoord(33, 29), new TileCoord(28, 33),
        };
        var grid = new TileGrid(GridSize, GridSize, walls);
        var world = new WorldState();
        var monster = SpawnMonster(world, home);
        var ai = CreateAi(seed: 999, grid, world);
        ai.Register(monster, serverTick: 0, pauseMinTicks: 1, pauseMaxTicks: 2, aggroScanIntervalTicks: 10);

        var sawSubTile = false;
        for (uint tick = 1; tick <= 4000; tick++)
        {
            ai.StepMonster(monster, tick, StepCooldownTicks, RoamTunables(roamRadius, 1, 2));
            AssertCollisionValid(monster.Position, grid.BlockedTiles, $"tick {tick}");

            var roundedCentre = WorldVector.FromTile(monster.Position.ToTileRounded());
            if (Distance(monster.Position, roundedCentre) > 1e-3)
            {
                sawSubTile = true;
            }
        }

        Assert.True(sawSubTile, "no hop ever landed sub-tile — continuous nav is not producing fractional positions.");
    }

    [Fact]
    public void IsDeterministicForAGivenSeed()
    {
        var path1 = RunSeededPath(seed: 42);
        var path2 = RunSeededPath(seed: 42);
        Assert.Equal(path1, path2);

        var path3 = RunSeededPath(seed: 43);
        Assert.NotEqual(path1, path3);
    }

    private static List<WorldVector> RunSeededPath(int seed)
    {
        const double roamRadius = 4d;
        var grid = OpenGrid();
        var world = new WorldState();
        var monster = SpawnMonster(world, new TileCoord(32, 32));
        var ai = CreateAi(seed, grid, world);
        ai.Register(monster, serverTick: 0, pauseMinTicks: 2, pauseMaxTicks: 4, aggroScanIntervalTicks: 10);

        var path = new List<WorldVector>();
        for (uint tick = 1; tick <= 500; tick++)
        {
            ai.StepMonster(monster, tick, StepCooldownTicks, RoamTunables(roamRadius, 2, 4));
            path.Add(monster.Position);
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
        Assert.Equal(WorldVector.FromTile(home), stored);
        Assert.True(ai.TryGetPhase(monster.Id, out var phase) && phase == MonsterRoamAi.State.Idle);
    }

    [Fact]
    public void TransitionsIdleToRoamingToIdleOnTheTimers()
    {
        const double roamRadius = 4d;
        var grid = OpenGrid();
        var world = new WorldState();
        var monster = SpawnMonster(world, new TileCoord(32, 32));
        var ai = CreateAi(seed: 5, grid, world);
        const uint pause = 10;
        ai.Register(monster, serverTick: 0, pauseMinTicks: pause, pauseMaxTicks: pause, aggroScanIntervalTicks: 10);

        for (uint tick = 1; tick < pause; tick++)
        {
            ai.StepMonster(monster, tick, StepCooldownTicks, RoamTunables(roamRadius, pause, pause));
            Assert.True(ai.TryGetPhase(monster.Id, out var phase) && phase == MonsterRoamAi.State.Idle,
                $"expected Idle before pause elapses (tick {tick}).");
        }

        ai.StepMonster(monster, pause, StepCooldownTicks, RoamTunables(roamRadius, pause, pause));
        Assert.True(ai.TryGetPhase(monster.Id, out var roamingPhase));
        Assert.Equal(MonsterRoamAi.State.Roaming, roamingPhase);

        var sawIdleAgain = false;
        for (uint tick = pause + 1; tick <= pause + 400; tick++)
        {
            ai.StepMonster(monster, tick, StepCooldownTicks, RoamTunables(roamRadius, pause, pause));
            if (ai.TryGetPhase(monster.Id, out var p) && p == MonsterRoamAi.State.Idle)
            {
                sawIdleAgain = true;
                break;
            }
        }

        Assert.True(sawIdleAgain, "monster never returned to Idle after roaming.");
    }

    // ---------------------------------------------------------------------------------------------------------
    // Aggro / chase / attack / de-aggro (Euclidean) / livelock.
    // ---------------------------------------------------------------------------------------------------------

    private static MonsterRoamAi.Tunables CombatTunables(
        double roamRadius = 4d,
        uint pauseMin = 100,
        uint pauseMax = 100,
        double aggroRadius = 6d,
        double deaggroRadius = 9d,
        double chaseLeash = 12d,
        double attackRangeUnits = 1.5d,
        int attackDamage = 10,
        uint attackCooldownTicks = 20,
        uint aggroScanInterval = 1)
        => new(
            roamRadius, pauseMin, pauseMax,
            aggroRadius, deaggroRadius, chaseLeash,
            attackRangeUnits, attackDamage, attackCooldownTicks, aggroScanInterval);

    // Wires the AI's aggro/resolve/attack callbacks to a real player WorldEntity, mirroring GameServer's continuous
    // path: findTarget — nearest alive player by Euclidean Position within the COARSE gather radius (the AI re-tests
    // the Euclidean aggro disc); tryResolve — the target's live Position + alive; attack — face + ApplyDamage.
    private static MonsterRoamAi CreateCombatAi(
        int seed, TileGrid grid, WorldState world, WorldEntity player, int[] hitCounter)
    {
        return CreateAi(
            seed, grid, world,
            findTarget: (WorldEntity monster, int gatherRadius, out ulong id, out WorldVector pos) =>
            {
                if (!world.TryGet(player.Id, out var p) || p.Stats.Health <= 0)
                {
                    id = 0;
                    pos = default;
                    return false;
                }

                // Coarse tile pre-filter (Chebyshev), matching GameServer's gather; the AI does the Euclidean test.
                var cheb = Math.Max(Math.Abs(p.TileCoord.X - monster.TileCoord.X), Math.Abs(p.TileCoord.Y - monster.TileCoord.Y));
                if (cheb > gatherRadius)
                {
                    id = 0;
                    pos = default;
                    return false;
                }

                id = p.Id;
                pos = p.Position;
                return true;
            },
            tryResolve: (ulong id, out WorldVector pos, out bool alive) =>
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
    public void AggrosWhenAPlayerEntersEuclideanRange()
    {
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
    public void DoesNotAggroAPlayerOutOfEuclideanRange()
    {
        var grid = OpenGrid();
        var world = new WorldState();
        var monster = SpawnMonster(world, new TileCoord(32, 32), networkId: 1);
        // 7 tiles east on a 6.0 Euclidean aggro radius — outside (and the gather pre-filter ⌈6⌉+1=7 would still gather
        // it, so this also pins that the AI's Euclidean test rejects an in-gather but out-of-disc target).
        var player = world.AddTransient(2, EntityKind.Player, "Hero", new TileCoord(39, 32), Direction8.S);
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
    public void ChaseEuclideanConvergesThenAttacksAtAttackRangeUnits()
    {
        // A monster aggros a player several tiles away and HOPS to close the Euclidean gap until within
        // AttackRangeUnits, then attacks (lands a hit) instead of hopping.
        var grid = OpenGrid();
        var world = new WorldState();
        var monster = SpawnMonster(world, new TileCoord(32, 32), networkId: 1);
        var player = world.AddTransient(2, EntityKind.Player, "Hero", new TileCoord(38, 32), Direction8.S);
        var hits = new int[1];
        var ai = CreateCombatAi(seed: 7, grid, world, player, hits);
        ai.Register(monster, serverTick: 0, pauseMinTicks: 100, pauseMaxTicks: 100, aggroScanIntervalTicks: 1);

        var startDist = Distance(monster.Position, player.Position);
        var minDist = startDist;
        for (uint tick = 1; tick <= 80; tick++)
        {
            ai.StepMonster(monster, tick, StepCooldownTicks, CombatTunables(attackDamage: 10, attackCooldownTicks: 20));
            minDist = Math.Min(minDist, Distance(monster.Position, player.Position));
        }

        Assert.True(minDist <= 1.5d + 1e-6, $"monster never closed to AttackRangeUnits (min Euclidean {minDist:F3}).");
        Assert.True(minDist < startDist, "monster did not move closer at all.");
        Assert.True(hits[0] > 0, "monster never landed a hit once in range.");
    }

    [Fact]
    public void AttacksWhenInRangeRespectsCooldownAndDealsDamage()
    {
        const int attackDamage = 10;
        const uint cooldown = 20;
        var grid = OpenGrid();
        var world = new WorldState();
        var monster = SpawnMonster(world, new TileCoord(32, 32), networkId: 1);
        var player = world.AddTransient(2, EntityKind.Player, "Hero", new TileCoord(33, 32), Direction8.S);
        var startHp = player.Stats.Health;
        var hits = new int[1];
        var ai = CreateCombatAi(seed: 7, grid, world, player, hits);
        ai.Register(monster, serverTick: 0, pauseMinTicks: 100, pauseMaxTicks: 100, aggroScanIntervalTicks: 1);

        const int ticks = 100;
        for (uint tick = 1; tick <= ticks; tick++)
        {
            ai.StepMonster(monster, tick, StepCooldownTicks, CombatTunables(attackDamage: attackDamage, attackCooldownTicks: cooldown));
        }

        // Adjacent (Euclidean 1.0 <= 1.5), so it attacks on cadence: hits at 1,21,41,61,81 → 5.
        Assert.Equal(5, hits[0]);
        Assert.Equal(startHp - 5 * attackDamage, world.TryGet(player.Id, out var p) ? p.Stats.Health : -1);
        Assert.Equal(Direction8.E, monster.Facing);
    }

    [Fact]
    public void DeaggrosWhenTargetIsRemovedAndReturnsHome()
    {
        var grid = OpenGrid();
        var world = new WorldState();
        var home = WorldVector.FromTile(new TileCoord(32, 32));
        var monster = SpawnMonster(world, home.ToTileRounded(), networkId: 1);
        var player = world.AddTransient(2, EntityKind.Player, "Hero", new TileCoord(36, 32), Direction8.S);
        var hits = new int[1];
        var ai = CreateCombatAi(seed: 7, grid, world, player, hits);
        ai.Register(monster, serverTick: 0, pauseMinTicks: 5, pauseMaxTicks: 5, aggroScanIntervalTicks: 1);

        for (uint tick = 1; tick <= 12; tick++)
        {
            ai.StepMonster(monster, tick, StepCooldownTicks, CombatTunables(pauseMin: 5, pauseMax: 5));
        }

        Assert.True(ai.TryGetPhase(monster.Id, out var chasing) && chasing == MonsterRoamAi.State.Chasing);
        Assert.True(Distance(monster.Position, home) > 0.5d, "monster should have left home while chasing.");

        world.Remove(player.Id, out _);
        var resumed = false;
        for (uint tick = 13; tick <= 300; tick++)
        {
            ai.StepMonster(monster, tick, StepCooldownTicks, CombatTunables(pauseMin: 5, pauseMax: 5));
            Assert.True(ai.TryGetPhase(monster.Id, out var phase));
            Assert.NotEqual(MonsterRoamAi.State.Chasing, phase);
            if (Distance(monster.Position, home) <= HopLocomotion.ProgressEpsilonUnits && phase == MonsterRoamAi.State.Idle)
            {
                resumed = true;
                break;
            }
        }

        Assert.True(resumed, "monster never returned home + resumed Idle after de-aggro.");
    }

    [Fact]
    public void DeaggrosWhenTargetLeavesEuclideanDeaggroRange()
    {
        var grid = OpenGrid();
        var world = new WorldState();
        var home = WorldVector.FromTile(new TileCoord(32, 32));
        var monster = SpawnMonster(world, home.ToTileRounded(), networkId: 1);
        var player = world.AddTransient(2, EntityKind.Player, "Hero", new TileCoord(35, 32), Direction8.S);
        var hits = new int[1];
        var ai = CreateCombatAi(seed: 7, grid, world, player, hits);
        ai.Register(monster, serverTick: 0, pauseMinTicks: 5, pauseMaxTicks: 5, aggroScanIntervalTicks: 1);

        ai.StepMonster(monster, serverTick: 1, StepCooldownTicks, CombatTunables(pauseMin: 5, pauseMax: 5));
        Assert.True(ai.TryGetPhase(monster.Id, out var chasing) && chasing == MonsterRoamAi.State.Chasing);

        // Teleport the player far east, well past the de-aggro range (9) and the chase leash (12).
        var before = player.TileCoord;
        player.TeleportTo(new TileCoord(60, 32));
        world.OnEntityMoved(player, before);
        Assert.True(Distance(monster.Position, player.Position) > 12d, "test setup: player not far enough.");

        var returned = false;
        for (uint tick = 2; tick <= 300; tick++)
        {
            ai.StepMonster(monster, tick, StepCooldownTicks, CombatTunables(pauseMin: 5, pauseMax: 5));
            if (ai.TryGetPhase(monster.Id, out var phase)
                && Distance(monster.Position, home) <= HopLocomotion.ProgressEpsilonUnits
                && phase == MonsterRoamAi.State.Idle)
            {
                returned = true;
                break;
            }
        }

        Assert.True(returned, "monster did not return home after the target left Euclidean de-aggro range.");
    }

    [Fact]
    public void DeaggrosWhenPulledBeyondEuclideanChaseLeash()
    {
        var grid = OpenGrid();
        var world = new WorldState();
        var home = WorldVector.FromTile(new TileCoord(20, 20));
        var monster = SpawnMonster(world, home.ToTileRounded(), networkId: 1);
        var player = world.AddTransient(2, EntityKind.Player, "Hero", new TileCoord(40, 20), Direction8.S);
        var hits = new int[1];
        var ai = CreateCombatAi(seed: 7, grid, world, player, hits);
        ai.Register(monster, serverTick: 0, pauseMinTicks: 5, pauseMaxTicks: 5, aggroScanIntervalTicks: 1);

        // Big aggro/de-aggro so it WOULD chase forever if not for the home leash; small chaseLeash (5).
        MonsterRoamAi.Tunables T() => CombatTunables(
            pauseMin: 5, pauseMax: 5, aggroRadius: 64d, deaggroRadius: 96d, chaseLeash: 5d);

        var returnedHome = false;
        for (uint tick = 1; tick <= 600; tick++)
        {
            ai.StepMonster(monster, tick, StepCooldownTicks, T());
            // Bounded by the chase leash + a one-hop overshoot tolerance.
            Assert.True(Distance(monster.Position, home) <= 5d + HopDistance + 1e-6,
                $"monster exceeded the Euclidean chase leash at tick {tick}: pos {monster.Position}, home {home}.");
            if (ai.TryGetPhase(monster.Id, out var phase)
                && Distance(monster.Position, home) <= HopLocomotion.ProgressEpsilonUnits
                && phase == MonsterRoamAi.State.Idle)
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
        var grid = OpenGrid();
        var world = new WorldState();
        var monster = SpawnMonster(world, new TileCoord(32, 32), networkId: 1);
        var player = world.AddTransient(2, EntityKind.Player, "Hero", new TileCoord(33, 32), Direction8.S);
        var hits = new int[1];
        var ai = CreateCombatAi(seed: 7, grid, world, player, hits);
        ai.Register(monster, serverTick: 0, pauseMinTicks: 100, pauseMaxTicks: 100, aggroScanIntervalTicks: 1);

        for (uint tick = 1; tick <= 500; tick++)
        {
            ai.StepMonster(monster, tick, StepCooldownTicks, CombatTunables(attackDamage: 9999, attackCooldownTicks: 1));
        }

        Assert.True(world.TryGet(player.Id, out var p), "player must still exist (no death/respawn).");
        Assert.Equal(0, p.Stats.Health);
    }

    [Fact]
    public void ChaseLivelockWatchdogFiresWhenWedged_NeverPenetratesAWall()
    {
        // The re-based livelock: the monster is BOXED so no hop (straight or fan) advances toward the target — a slide
        // fixpoint. The watchdog must bail it off the chase (it cannot freeze), and it must NEVER penetrate a wall.
        //
        // Layout: monster at (10,10), walls on ALL EIGHT neighbours (cardinals AND corners) so it is fully enclosed —
        // a hop in any direction slides back to the centre (zero progress). At body radius 0.5 a four-cardinal box
        // would leak diagonally through the open corner (the resolver legitimately rounds the corner), so the corners
        // must be walled too for a true enclosure. Target sits outside → Stuck every cadence → the watchdog fires.
        var walls = EnclosingWalls(new TileCoord(10, 10));
        var grid = new TileGrid(GridSize, GridSize, walls);
        var world = new WorldState();
        var monster = SpawnMonster(world, new TileCoord(10, 10), networkId: 1);
        var player = world.AddTransient(2, EntityKind.Player, "Hero", new TileCoord(15, 10), Direction8.S);
        var hits = new int[1];
        var ai = CreateCombatAi(seed: 7, grid, world, player, hits);
        ai.Register(monster, serverTick: 0, pauseMinTicks: 5, pauseMaxTicks: 5, aggroScanIntervalTicks: 1);

        // Force a chase by aggroing (the box neighbours leave the centre open; aggro at Euclidean 5 with radius 6).
        ai.StepMonster(monster, serverTick: 1, StepCooldownTicks, CombatTunables(pauseMin: 5, pauseMax: 5));
        Assert.True(ai.TryGetPhase(monster.Id, out var chasing) && chasing == MonsterRoamAi.State.Chasing);

        var bailed = false;
        for (uint tick = 2; tick <= 80; tick++)
        {
            ai.StepMonster(monster, tick, StepCooldownTicks, CombatTunables(pauseMin: 5, pauseMax: 5));
            AssertCollisionValid(monster.Position, grid.BlockedTiles, $"tick {tick}");
            if (ai.TryGetPhase(monster.Id, out var phase) && phase != MonsterRoamAi.State.Chasing)
            {
                bailed = true;
                break;
            }
        }

        Assert.True(bailed, "wedged monster froze in chase — the no-progress watchdog failed to fire.");
        AssertCollisionValid(monster.Position, grid.BlockedTiles, "final");
    }

    [Fact]
    public void RoamLivelockWatchdogRecoversAWedgedRoamer_NeverPenetratesAWall()
    {
        // A roaming monster boxed on all eight neighbours: whenever it picks a roam target outside the box its hop
        // slides to a fixpoint, the watchdog bails it back to Idle, and it re-picks. Over a long run it must never
        // penetrate a wall and must keep cycling (observed Idle after the early ticks, i.e. not permanently Roaming).
        var walls = EnclosingWalls(new TileCoord(10, 10));
        var grid = new TileGrid(GridSize, GridSize, walls);
        var world = new WorldState();
        var monster = SpawnMonster(world, new TileCoord(10, 10), networkId: 1);
        var ai = CreateAi(seed: 3, grid, world);
        ai.Register(monster, serverTick: 0, pauseMinTicks: 2, pauseMaxTicks: 2, aggroScanIntervalTicks: 10);

        // Roam radius 4 so the picker finds walkable tiles BEYOND the 8-wall box (the monster tries to hop to them,
        // wedges, the watchdog returns it to Idle, it re-picks — the cycle under test). It can never actually leave the
        // box, so it must keep cycling Roaming→(wedge)→Idle without ever penetrating a wall.
        var sawRoaming = false;
        var sawIdleAfterRoaming = false;
        for (uint tick = 1; tick <= 2000; tick++)
        {
            ai.StepMonster(monster, tick, StepCooldownTicks, RoamTunables(4d, 2, 2));
            AssertCollisionValid(monster.Position, grid.BlockedTiles, $"tick {tick}");
            if (ai.TryGetPhase(monster.Id, out var phase))
            {
                if (phase == MonsterRoamAi.State.Roaming)
                {
                    sawRoaming = true;
                }
                else if (phase == MonsterRoamAi.State.Idle && sawRoaming && tick > 50)
                {
                    sawIdleAfterRoaming = true;
                }
            }
        }

        Assert.True(sawRoaming, "monster never entered Roaming — the picker should hand it far targets to wedge on.");
        Assert.True(sawIdleAfterRoaming, "roaming monster appears frozen (never recovered to Idle after wedging).");
    }
}
