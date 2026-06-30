using System;
using System.Collections.Generic;
using Mmo.Server.Runtime;
using Mmo.Shared.Domain;
using Xunit;

namespace Mmo.Server.Tests;

// MONSTER-SEPARATION (todo/N-monster-monster-collision-separation.md): headless tests for the server-authoritative
// monster↔monster de-penetration pass (MonsterSeparation). Mirrors the GlideLocomotion test setup — a bare TileGrid +
// bare WorldState + the injected wall-query / neighbour-query / apply-landing seams (no live Zone/GameServer) — and
// pins the contract: overlapping pairs separate to ≥ 2×radius; a push toward a wall never penetrates it; a tight
// cluster de-penetrates without exploding/oscillating; exact overlap splits deterministically (no NaN); an idle
// (Velocity 0) nudge bumps StateRevision so it replicates; the pass introduces NO velocity; players are not moved.
public sealed class MonsterSeparationTests
{
    private const int GridSize = 64;
    private const double BodyRadius = 0.5d;          // the shared wall + monster-monster body radius.
    private const double MinDist = 2d * BodyRadius;  // 1.0 — two bodies overlap below this centre distance.

    private static TileGrid OpenGrid() => new(GridSize, GridSize, []);

    private static double Distance(WorldVector a, WorldVector b) => (a - b).Length;

    // The apply-landing seam, wired EXACTLY like Zone.ApplyMonsterLanding: apply the resolved position and migrate the
    // spatial-grid bucket on a tile cross (so the neighbour query stays consistent).
    private static Func<WorldEntity, WorldVector, bool> ApplyLanding(WorldState world)
        => (entity, landing) =>
        {
            var previous = entity.TileCoord;
            var crossed = entity.ApplyResolvedMove(landing);
            if (crossed)
            {
                world.OnEntityMoved(entity, previous);
            }

            return crossed;
        };

    // Builds a MonsterSeparation wired like GameServer: the live body radius, the spatial neighbour query
    // (WorldState.GatherInterestCandidates), the shared wall query, and the apply-landing seam.
    private static MonsterSeparation CreateSeparation(TileGrid grid, WorldState world)
        => new(
            () => BodyRadius,
            world.GatherInterestCandidates,
            grid.QueryNearbyWalls,
            ApplyLanding(world));

    // Spawns a monster and places it at an EXACT sub-tile position (via the same apply seam, so the grid bucket
    // matches its rounded tile). Velocity stays Zero (default) — these are idle monsters.
    private static WorldEntity SpawnMonsterAt(WorldState world, WorldVector position, uint networkId)
    {
        var tile = position.ToTileRounded();
        var monster = world.AddTransient(networkId, EntityKind.Monster, "Slime", tile, Direction8.S);
        ApplyLanding(world)(monster, position);
        return monster;
    }

    private static List<WorldEntity> GatherMonsters(WorldState world)
    {
        var list = new List<WorldEntity>();
        world.CopyMonstersTo(list);
        return list;
    }

    // Assert the body centre is collision-valid: at least `radius` (minus a float tolerance) from every blocked tile's
    // 1×1 AABB — the resolver's invariant (a separation nudge must never land the circle penetrating a wall).
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
    public void TwoOverlappingMonsters_SeparateToAtLeastMinDistance()
    {
        var grid = OpenGrid();
        var world = new WorldState();
        // Centre the pair on a tile BOUNDARY so the symmetric push lands each cleanly (no rounding surprises); centre
        // distance 0.3 < 1.0 → they overlap.
        var a = SpawnMonsterAt(world, new WorldVector(32.35d, 32d), 1);
        var b = SpawnMonsterAt(world, new WorldVector(32.65d, 32d), 2);
        var separation = CreateSeparation(grid, world);
        var monsters = GatherMonsters(world);

        Assert.True(Distance(a.Position, b.Position) < MinDist, "precondition: the pair must start overlapping.");

        for (uint tick = 1; tick <= 5; tick++)
        {
            separation.Separate(monsters);
        }

        Assert.True(Distance(a.Position, b.Position) >= MinDist - 1e-9,
            $"pair did not separate to >= {MinDist} (got {Distance(a.Position, b.Position):F4}).");
    }

    [Fact]
    public void PushTowardWall_NeverPenetratesIt()
    {
        // A wall to the EAST. Two monsters overlap to its west; separation pushes the eastern one TOWARD the wall, but
        // the shared resolver must wall-clamp it so the body never penetrates. Assert collision-valid EVERY tick.
        var wall = new TileCoord(34, 32);
        var grid = new TileGrid(GridSize, GridSize, new[] { wall });
        var world = new WorldState();
        var a = SpawnMonsterAt(world, new WorldVector(32.9d, 32d), 1); // nearer the wall
        var b = SpawnMonsterAt(world, new WorldVector(32.5d, 32d), 2); // overlaps a (0.4 < 1.0)
        var separation = CreateSeparation(grid, world);
        var monsters = GatherMonsters(world);

        for (uint tick = 1; tick <= 20; tick++)
        {
            separation.Separate(monsters);
            AssertCollisionValid(a.Position, grid.BlockedTiles, $"tick {tick} (a)");
            AssertCollisionValid(b.Position, grid.BlockedTiles, $"tick {tick} (b)");
        }
    }

    [Fact]
    public void Cluster_DePenetratesWithoutExploding_AndStaysBounded()
    {
        var grid = OpenGrid();
        var world = new WorldState();
        var center = new WorldVector(32d, 32d);
        var monsters = new List<WorldEntity>();
        // 8 monsters stacked within ~0.1 of one point (tiny distinct offsets — deterministic, all overlapping).
        for (uint i = 0; i < 8; i++)
        {
            var angle = i * (2d * Math.PI / 8d);
            var pos = new WorldVector(center.X + 0.05d * Math.Cos(angle), center.Y + 0.05d * Math.Sin(angle));
            monsters.Add(SpawnMonsterAt(world, pos, i + 1));
        }

        var separation = CreateSeparation(grid, world);

        double MinPairwise()
        {
            var min = double.MaxValue;
            for (var i = 0; i < monsters.Count; i++)
            {
                for (var j = i + 1; j < monsters.Count; j++)
                {
                    min = Math.Min(min, Distance(monsters[i].Position, monsters[j].Position));
                }
            }

            return min;
        }

        var prevMin = MinPairwise();
        for (uint tick = 1; tick <= 80; tick++)
        {
            separation.Separate(monsters);

            // Bounded: no NaN/Inf, and no explosion (every body stays within a sane box of the cluster origin).
            foreach (var m in monsters)
            {
                Assert.True(double.IsFinite(m.Position.X) && double.IsFinite(m.Position.Y),
                    $"tick {tick}: body {m.Id} went non-finite ({m.Position}).");
                Assert.True(Distance(m.Position, center) < 10d,
                    $"tick {tick}: body {m.Id} exploded away from the cluster ({m.Position}).");
            }

            // Monotonically improving: the tightest pair never gets meaningfully tighter tick-over-tick (no oscillation
            // collapsing them back together). Separation only ever PUSHES overlapping bodies apart (never pulls), so the
            // min strictly grows toward equilibrium; the tolerance only absorbs float dust, not a real collapse.
            var min = MinPairwise();
            Assert.True(min >= prevMin - 1e-6,
                $"tick {tick}: cluster oscillated — min pairwise distance dropped {prevMin:F4} -> {min:F4}.");
            prevMin = min;
        }

        // After settling, the tightest pair is at least ~a body radius apart (a tight blob need not reach the full
        // 1.0, but it must be well de-penetrated) and the cluster clearly expanded from its stacked start.
        Assert.True(prevMin >= BodyRadius - 1e-6,
            $"cluster did not de-penetrate to >= {BodyRadius} (min pairwise {prevMin:F4}).");
    }

    [Fact]
    public void ExactOverlap_SplitsDeterministically_NoNaN()
    {
        WorldVector RunOnce()
        {
            var grid = OpenGrid();
            var world = new WorldState();
            var a = SpawnMonsterAt(world, new WorldVector(32d, 32d), 1);
            var b = SpawnMonsterAt(world, new WorldVector(32d, 32d), 2); // EXACT same point.
            var separation = CreateSeparation(grid, world);
            var monsters = GatherMonsters(world);

            separation.Separate(monsters);

            Assert.True(double.IsFinite(a.Position.X) && double.IsFinite(a.Position.Y), "a went non-finite.");
            Assert.True(double.IsFinite(b.Position.X) && double.IsFinite(b.Position.Y), "b went non-finite.");
            var d = Distance(a.Position, b.Position);
            Assert.True(d > 1e-6, $"exact-overlap pair did not split (distance {d:F6}).");
            return b.Position - a.Position; // the split vector, for the determinism check.
        }

        var first = RunOnce();
        var second = RunOnce();
        Assert.Equal(first.X, second.X, 12); // reproducible — same split both runs (no RNG).
        Assert.Equal(first.Y, second.Y, 12);
    }

    [Fact]
    public void IdleNudge_BumpsStateRevision_SoItReplicates()
    {
        var grid = OpenGrid();
        var world = new WorldState();
        // Pair centred on a tile boundary so each lands within its OWN tile (no tile cross → ApplyResolvedMove does not
        // bump StateRevision on its own; the pass's explicit MarkRepositioned must).
        var a = SpawnMonsterAt(world, new WorldVector(32.35d, 32d), 1); // rounds to tile 32
        var b = SpawnMonsterAt(world, new WorldVector(32.65d, 32d), 2); // rounds to tile 33
        var separation = CreateSeparation(grid, world);
        var monsters = GatherMonsters(world);

        Assert.Equal(WorldVector.Zero, a.Velocity); // idle — the re-include risk this test pins.
        Assert.Equal(WorldVector.Zero, b.Velocity);
        var revA = a.StateRevision;
        var revB = b.StateRevision;
        var tileA = a.TileCoord;
        var tileB = b.TileCoord;

        separation.Separate(monsters);

        // They moved (de-penetrated) but did NOT cross a tile, so only the explicit revision bump re-includes them.
        Assert.Equal(tileA, a.TileCoord);
        Assert.Equal(tileB, b.TileCoord);
        Assert.True(a.StateRevision > revA, "idle nudged monster A did not bump StateRevision — its correction won't replicate.");
        Assert.True(b.StateRevision > revB, "idle nudged monster B did not bump StateRevision — its correction won't replicate.");
    }

    [Fact]
    public void Separation_IntroducesNoVelocity()
    {
        var grid = OpenGrid();
        var world = new WorldState();
        var a = SpawnMonsterAt(world, new WorldVector(32d, 32d), 1);
        var b = SpawnMonsterAt(world, new WorldVector(32.3d, 32d), 2);
        var separation = CreateSeparation(grid, world);
        var monsters = GatherMonsters(world);

        for (uint tick = 1; tick <= 5; tick++)
        {
            separation.Separate(monsters);
        }

        Assert.Equal(WorldVector.Zero, a.Velocity); // pure position de-penetration — no physics.
        Assert.Equal(WorldVector.Zero, b.Velocity);
    }

    [Fact]
    public void Players_AreNotMovedByThePass()
    {
        var grid = OpenGrid();
        var world = new WorldState();
        // Two monsters that WILL separate, plus a player overlapping one of them. The player must be untouched.
        var m1 = SpawnMonsterAt(world, new WorldVector(32d, 32d), 1);
        var m2 = SpawnMonsterAt(world, new WorldVector(32.3d, 32d), 2);
        var player = world.AddPlayer(
            3, Guid.NewGuid(), "Player", new TileCoord(32, 32),
            new ClientSession(null!), new Inventory(ItemRegistry.Default));
        var playerStart = player.Position;

        var separation = CreateSeparation(grid, world);
        var monsters = GatherMonsters(world);
        Assert.DoesNotContain(player, monsters); // the participant gather is monster-only.

        for (uint tick = 1; tick <= 5; tick++)
        {
            separation.Separate(monsters);
        }

        Assert.Equal(playerStart, player.Position); // player overlapping a monster is untouched.
        // And the monsters did separate (sanity — the pass actually ran).
        Assert.True(Distance(m1.Position, m2.Position) >= MinDist - 1e-9);
    }
}
