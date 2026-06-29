using System;
using System.Collections.Generic;
using Mmo.Server.Runtime;
using Mmo.Shared.Domain;
using Xunit;

namespace Mmo.Server.Tests;

// MONSTER-BEHAVIOR P2 (docs/monster-behavior-design.md): headless tests for the CONTINUOUS-WALK locomotion
// (GlideLocomotion) — the first body that moves EVERY tick (a walk) instead of a discrete cadence-gated hop. Mirrors
// the HopLocomotion test setup: a bare TileGrid + a bare WorldEntity + the injected wall-query / apply-landing
// delegates (no live Zone/GameServer). Pins the P2 contract: a glider integrates toward its target each tick (monotonic
// approach), CLAMPS the final step so it never overshoots a near target, SLIDES along a wall through the shared
// resolver (never penetrates it), SETS Velocity = heading × speed while moving (so it replicates + extrapolates with no
// protocol change), Stop() zeroes that Velocity, returns Moved on real progress / Stuck when fully wedged, and NEVER
// returns OnCooldown (a glide has no cadence).
public sealed class GlideLocomotionTests
{
    private const int GridSize = 64;
    private const double BodyRadius = 0.5d;          // the player body radius the glider also collides at.
    private const int TickRate = 20;                 // server tick rate → dt = 1/20 = 0.05 s.
    private const double Speed = 4.0d;               // tiles/sec walk speed → 0.2 tiles per tick.
    private const double StepPerTick = Speed / TickRate;

    private static TileGrid OpenGrid() => new(GridSize, GridSize, []);

    private static double Distance(WorldVector a, WorldVector b) => (a - b).Length;

    // Builds a GlideLocomotion wired exactly like GameServer: the SAME shared wall query + body radius ordinary
    // movement uses, and the SAME apply seam (ApplyResolvedMove + spatial-bucket migration on a tile cross).
    private static GlideLocomotion CreateGlide(TileGrid grid, WorldState world)
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
            TickRate);

    // A monster at `tile`'s centre with the walk speed seeded (GameServer seeds SpeedUnitsPerSecond at spawn via
    // RefreshSpeedStat; here we set it directly so the bare entity has a non-zero walk speed).
    private static WorldEntity SpawnGlider(WorldState world, TileCoord tile, uint networkId = 1)
    {
        var monster = world.AddTransient(networkId, EntityKind.Monster, "Gnoll", tile, Direction8.S);
        monster.SetSpeedUnitsPerSecond(Speed);
        return monster;
    }

    // Assert the body centre is collision-valid: at least `radius` (minus a float tolerance) from every blocked tile's
    // 1×1 AABB — the resolver's invariant (a glide step must never land the circle penetrating a wall).
    private static void AssertCollisionValid(WorldVector pos, IReadOnlySet<TileCoord> blocked, string where)
    {
        const double tol = 1e-6;
        foreach (var tile in blocked)
        {
            var cx = Math.Clamp(pos.X, tile.X - 0.5d, tile.X + 0.5d);
            var cy = Math.Clamp(pos.Y, tile.Y - 0.5d, tile.Y + 0.5d);
            var d = Distance(pos, new WorldVector(cx, cy));
            Assert.True(d >= BodyRadius - tol,
                $"{where}: body centre {pos} penetrates blocked tile {tile} (dist {d:F4} < radius {BodyRadius}).");
        }
    }

    [Fact]
    public void GlidesTowardTargetEachTick_MonotonicApproach_NeverOnCooldown()
    {
        var grid = OpenGrid();
        var world = new WorldState();
        var monster = SpawnGlider(world, new TileCoord(32, 32));
        var glide = CreateGlide(grid, world);
        var target = new WorldVector(monster.Position.X + 8d, monster.Position.Y); // 8 tiles due east.

        var prevDist = Distance(monster.Position, target);
        for (uint tick = 1; tick <= 30; tick++)
        {
            var result = glide.Advance(monster, target, tick, cooldownTicks: 7);
            Assert.NotEqual(HopResult.OnCooldown, result); // a glide has no cadence — never waits.
            Assert.Equal(HopResult.Moved, result);

            var dist = Distance(monster.Position, target);
            Assert.True(dist < prevDist - 1e-9, $"glider did not strictly approach the target at tick {tick}.");
            prevDist = dist;

            // It advanced ~one step (open field, far target → unclamped) and SET the replicated walk velocity.
            Assert.Equal(StepPerTick, monster.Position.X - (32d + (tick - 1) * StepPerTick), 1e-6);
            Assert.Equal(Speed, monster.Velocity.X, 1e-9); // dir (1,0) × speed.
            Assert.Equal(0d, monster.Velocity.Y, 1e-9);
        }
    }

    [Fact]
    public void ClampsTheFinalStep_DoesNotOvershootANearTarget()
    {
        var grid = OpenGrid();
        var world = new WorldState();
        var monster = SpawnGlider(world, new TileCoord(32, 32));
        var glide = CreateGlide(grid, world);

        // Target 0.15 east — closer than one full step (0.2), so the step MUST clamp to 0.15 and land exactly on it
        // (not 0.2 past it). 0.15 >= the progress epsilon (0.1), so it still counts as a real Moved.
        var origin = monster.Position;
        var target = new WorldVector(origin.X + 0.15d, origin.Y);

        Assert.Equal(HopResult.Moved, glide.Advance(monster, target, serverTick: 1, cooldownTicks: 7));
        Assert.Equal(target.X, monster.Position.X, 1e-9);   // landed exactly on the target, not past it.
        Assert.Equal(target.Y, monster.Position.Y, 1e-9);
        Assert.True(monster.Position.X <= target.X + 1e-9, "glider overshot the near target (clamp failed).");
    }

    [Fact]
    public void SetsVelocityWhileMoving_AndStopZeroesIt()
    {
        var grid = OpenGrid();
        var world = new WorldState();
        var monster = SpawnGlider(world, new TileCoord(32, 32));
        var glide = CreateGlide(grid, world);
        var target = new WorldVector(monster.Position.X, monster.Position.Y + 8d); // due north.

        Assert.Equal(HopResult.Moved, glide.Advance(monster, target, serverTick: 1, cooldownTicks: 7));
        // Velocity = dir (0,1) × speed — the replicated walk velocity the client extrapolates.
        Assert.Equal(0d, monster.Velocity.X, 1e-9);
        Assert.Equal(Speed, monster.Velocity.Y, 1e-9);

        glide.Stop(monster);
        Assert.Equal(WorldVector.Zero, monster.Velocity); // Stop parks it (client stops extrapolating).
    }

    [Fact]
    public void SlidesAlongAWall_NeverPenetratesIt_AndStillMakesProgress()
    {
        // A wall sits directly between the glider and a target to the NE. A straight step is blocked on the X axis, but
        // the resolver SLIDES the glider along the wall's face (Y is free) — so it follows the wall (no fan), never
        // penetrates it, and still nets progress toward the target rather than freezing.
        var wall = new TileCoord(33, 32);
        var grid = new TileGrid(GridSize, GridSize, new[] { wall });
        var world = new WorldState();
        var monster = SpawnGlider(world, new TileCoord(32, 32));
        var glide = CreateGlide(grid, world);
        var target = new WorldVector(34d, 40d); // up and to the right, straight line crosses the wall.

        var startDist = Distance(monster.Position, target);
        for (uint tick = 1; tick <= 60; tick++)
        {
            glide.Advance(monster, target, tick, cooldownTicks: 7);
            AssertCollisionValid(monster.Position, grid.BlockedTiles, $"tick {tick}");
        }

        Assert.True(Distance(monster.Position, target) < startDist - 1e-6,
            "glider made no net progress toward the target — it should slide along the wall, not freeze.");
        Assert.True(monster.Position.Y > 32d + 1e-6, "glider did not slide north along the wall face.");
    }

    [Fact]
    public void ReturnsStuckWhenFullyWedged_NeverPenetratesAWall()
    {
        // Boxed on all eight neighbours: no step in any direction advances toward an outside target — every step
        // resolves back to (within epsilon of) the centre. The glider must report Stuck (the AI's watchdog bails it)
        // and must NEVER penetrate a wall.
        var walls = new List<TileCoord>();
        for (var dy = -1; dy <= 1; dy++)
        {
            for (var dx = -1; dx <= 1; dx++)
            {
                if (dx != 0 || dy != 0)
                {
                    walls.Add(new TileCoord(10 + dx, 10 + dy));
                }
            }
        }

        var grid = new TileGrid(GridSize, GridSize, walls.ToArray());
        var world = new WorldState();
        var monster = SpawnGlider(world, new TileCoord(10, 10));
        var glide = CreateGlide(grid, world);
        var target = new WorldVector(20d, 10d); // well outside the box.

        for (uint tick = 1; tick <= 10; tick++)
        {
            Assert.Equal(HopResult.Stuck, glide.Advance(monster, target, tick, cooldownTicks: 7));
            AssertCollisionValid(monster.Position, grid.BlockedTiles, $"tick {tick}");
        }

        // VELOCITY COHERENCE (replication guardrail): a wedged glider replicates ~ZERO velocity (it isn't moving) —
        // NOT the desired dir×speed pointing INTO the wall. Otherwise the client (which extrapolates along velocity)
        // would drift into the wall each tick. Pins the (landing-from)/dt resolved-velocity fix.
        Assert.True(
            monster.Velocity.Length < 1e-6,
            $"wedged glider replicated a non-zero velocity ({monster.Velocity.X:F3},{monster.Velocity.Y:F3}) — should be ~0 (it's not actually moving).");
    }

    [Fact]
    public void ReturnsStuckWhenAlreadyOnTarget()
    {
        // Guard: the AI checks arrival/adjacency first, but if Advance is called with target == position there is no
        // heading — it must not divide-by-zero or move; it reports Stuck (no progress).
        var grid = OpenGrid();
        var world = new WorldState();
        var monster = SpawnGlider(world, new TileCoord(32, 32));
        var glide = CreateGlide(grid, world);

        var result = glide.Advance(monster, monster.Position, serverTick: 1, cooldownTicks: 7);
        Assert.Equal(HopResult.Stuck, result);
    }
}
