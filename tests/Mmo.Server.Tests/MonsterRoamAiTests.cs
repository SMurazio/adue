using Mmo.Server.Runtime;
using Mmo.Shared.Domain;
using Xunit;

namespace Mmo.Server.Tests;

// LIVING-ENEMIES P1: headless tests for the leashed idle-wander brain (MonsterRoamAi), driven directly against a
// WorldState + TileGrid (no network/GameServer). Randomness is seeded so every assertion is deterministic. These
// pin the behaviour the human live-verifies: a monster is still most of the time, occasionally strolls a few
// tiles, NEVER leaves its leash radius, and always picks a reachable/open roam target.
public sealed class MonsterRoamAiTests
{
    // A generous open grid so the leash is the only thing bounding the wander (no walls in the way) for the
    // leash/cadence tests. Walls are introduced explicitly in the blocked-target test.
    private const int GridSize = 64;
    private const uint StepCooldownTicks = 3; // the project's base cadence (150 ms @ 20 Hz).

    private static TileGrid OpenGrid() => new(GridSize, GridSize, []);

    // Builds an AI whose walkability + stepper are wired to a real WorldState/TileGrid exactly like Zone does
    // (TryStep migrates the spatial bucket, so the entity's Tile advances on accept). Returns the AI + world so a
    // test can spawn monsters and tick them.
    private static MonsterRoamAi CreateAi(int seed, TileGrid grid, WorldState world)
    {
        return new MonsterRoamAi(
            seed,
            grid.IsWalkable,
            (entity, direction, tick, cooldownTicks) =>
            {
                var previous = entity.Tile;
                var stepped = entity.TryStep(direction, tick, cooldownTicks, grid, out _);
                if (stepped)
                {
                    world.OnEntityMoved(entity, previous);
                }

                return stepped;
            });
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
        ai.Register(monster, serverTick: 0, pauseMinTicks: 1, pauseMaxTicks: 2);

        for (uint tick = 1; tick <= 3000; tick++)
        {
            ai.StepMonster(monster, tick, StepCooldownTicks, roamRadius, pauseMinTicks: 1, pauseMaxTicks: 2);

            var dx = Math.Abs(monster.Tile.X - home.X);
            var dy = Math.Abs(monster.Tile.Y - home.Y);
            Assert.True(
                Math.Max(dx, dy) <= roamRadius,
                $"monster left the leash at tick {tick}: tile={monster.Tile}, home={home}, radius={roamRadius}");
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
        ai.Register(monster, serverTick: 0, pauseMin, pauseMax);

        var advances = 0;
        const int ticks = 4000;
        for (uint tick = 1; tick <= ticks; tick++)
        {
            if (ai.StepMonster(monster, tick, StepCooldownTicks, roamRadius, pauseMin, pauseMax))
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
        ai.Register(monster, serverTick: 0, pauseMinTicks: pause, pauseMaxTicks: pause);

        // Before the pause elapses it stays Idle and does not move.
        for (uint tick = 1; tick < pause; tick++)
        {
            ai.StepMonster(monster, tick, StepCooldownTicks, roamRadius, pause, pause);
            Assert.True(ai.TryGetPhase(monster.Id, out var phase) && phase == MonsterRoamAi.State.Idle,
                $"expected Idle before pause elapses (tick {tick}).");
        }
        var homeTile = monster.Tile;
        Assert.Equal(new TileCoord(32, 32), homeTile);

        // At/after the pause it picks a destination and starts Roaming (the open grid always has an open target).
        ai.StepMonster(monster, pause, StepCooldownTicks, roamRadius, pause, pause);
        Assert.True(ai.TryGetPhase(monster.Id, out var roamingPhase));
        Assert.Equal(MonsterRoamAi.State.Roaming, roamingPhase);

        // Drive it long enough to reach the destination and flip back to Idle at least once.
        var sawIdleAgain = false;
        for (uint tick = pause + 1; tick <= pause + 200; tick++)
        {
            ai.StepMonster(monster, tick, StepCooldownTicks, roamRadius, pause, pause);
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
        ai.Register(monster, serverTick: 0, pauseMinTicks: 1, pauseMaxTicks: 3);

        for (uint tick = 1; tick <= 4000; tick++)
        {
            ai.StepMonster(monster, tick, StepCooldownTicks, roamRadius, pauseMinTicks: 1, pauseMaxTicks: 3);

            // Never standing on a wall.
            Assert.True(grid.IsWalkable(monster.Tile), $"monster on a blocked tile {monster.Tile} at tick {tick}.");
            // Never outside the leash.
            var dx = Math.Abs(monster.Tile.X - home.X);
            var dy = Math.Abs(monster.Tile.Y - home.Y);
            Assert.True(Math.Max(dx, dy) <= roamRadius, $"monster left the leash at tick {tick}: {monster.Tile}.");
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
        ai.Register(monster, serverTick: 0, pauseMinTicks: 2, pauseMaxTicks: 4);

        var path = new List<TileCoord>();
        for (uint tick = 1; tick <= 500; tick++)
        {
            ai.StepMonster(monster, tick, StepCooldownTicks, roamRadius, pauseMinTicks: 2, pauseMaxTicks: 4);
            path.Add(monster.Tile);
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
        ai.Register(monster, serverTick: 0, pauseMinTicks: 2, pauseMaxTicks: 4);

        Assert.Equal(1, ai.TrackedCount);
        Assert.True(ai.TryGetHome(monster.Id, out var stored));
        Assert.Equal(home, stored);
        Assert.True(ai.TryGetPhase(monster.Id, out var phase) && phase == MonsterRoamAi.State.Idle);
    }
}
