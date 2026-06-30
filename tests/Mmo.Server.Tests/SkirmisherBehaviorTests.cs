using Mmo.Server.Runtime;
using Mmo.Shared.Domain;
using Xunit;

namespace Mmo.Server.Tests;

// MONSTER-BEHAVIOR P4 (docs/monster-behavior-design.md): headless tests for the gnoll's brain (SkirmisherBehavior) —
// the first genuinely per-type behavior. A skirmisher inherits the ENTIRE BasicRoamer state machine (roam / aggro /
// chase / attack / leash / watchdog) and differs in exactly ONE way: when WOUNDED (Health <= FleeHealthPct*MaxHealth)
// it FLEES from the chased target — glides directly AWAY — and does NOT attack that tick. These pin: it flees + skips
// the attack below the threshold; it is byte-identical to a BasicRoamer above the threshold (the override is inert);
// FleeHealthPct 0 never flees even at 1 HP; and a flee move stays collision-valid (stops at a wall, never penetrates).
// The brain expresses flee ONLY through the handed-in (velocity-coherent) GlideLocomotion, so it replicates with no
// protocol change. Driven directly against a WorldState + TileGrid + the real GlideLocomotion (no network/GameServer).
public sealed class SkirmisherBehaviorTests
{
    private const int GridSize = 64;
    private const uint StepCooldownTicks = 3;     // the project's base cadence (the glide's watchdog window).
    private const double BodyRadius = 0.5d;        // the player body radius the monster also collides at.
    private const int TickRate = 20;               // server tick rate (fixes dt for the glide integration).
    private const double WalkSpeed = 4.0d;         // a non-zero walk speed (a glider can't move without one).

    private static TileGrid OpenGrid() => new(GridSize, GridSize, []);

    private static double Distance(WorldVector a, WorldVector b) => (a - b).Length;

    private static WorldEntity SpawnMonster(WorldState world, TileCoord tile, int health, int maxHealth, uint networkId = 1)
    {
        var monster = world.AddTransient(networkId, EntityKind.Monster, "Monster", tile, Direction8.S);
        monster.SetSpeedUnitsPerSecond(WalkSpeed);
        monster.SetMaxHealthFull(maxHealth);
        if (health < maxHealth)
        {
            monster.ApplyDamage(maxHealth - health); // drop to the wounded HP under test.
        }

        return monster;
    }

    // A GlideLocomotion wired exactly like GameServer / the BasicRoamer glide tests: the SAME shared wall query + body
    // radius + apply seam ordinary movement uses, dt fixed by the tick rate. The skirmisher expresses flee through this.
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
            TickRate,
            _ => false); // MONSTER-BEHAVIOR P5: no executor wired into these glide tests → never action-active.

    // Builds a behavior (a SkirmisherBehavior, or a plain BasicRoamerBehavior for the inertness comparison) wired to a
    // live player target + a hit counter, mirroring GameServer's continuous combat path (findTarget by Euclidean
    // Position within the coarse gather radius; tryResolve to the live Position + alive; attack = count + ApplyDamage).
    private static BasicRoamerBehavior CreateBehavior(
        int seed, TileGrid grid, WorldState world, WorldEntity player, int[] hitCounter, bool skirmisher)
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

                var cheb = System.Math.Max(
                    System.Math.Abs(p.TileCoord.X - monster.TileCoord.X),
                    System.Math.Abs(p.TileCoord.Y - monster.TileCoord.Y));
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
            ? new SkirmisherBehavior(seed, grid.IsWalkable, findTarget, tryResolve, attack)
            : new BasicRoamerBehavior(seed, grid.IsWalkable, findTarget, tryResolve, attack);
    }

    // Combat tunables with a configurable flee threshold. Big de-aggro/leash by default so a fleeing monster keeps
    // running for the whole test window (it never gives up + returns home mid-assertion).
    private static MonsterAiTunables Tunables(
        double fleeHealthPct,
        double aggroRadius = 6d,
        double deaggroRadius = 50d,
        double chaseLeash = 50d,
        double attackRangeUnits = 1.5d,
        int attackDamage = 10,
        uint attackCooldownTicks = 20,
        uint aggroScanInterval = 1,
        double roamRadius = 4d,
        uint pauseMin = 100,
        uint pauseMax = 100)
        => new(
            roamRadius, pauseMin, pauseMax,
            aggroRadius, deaggroRadius, chaseLeash,
            attackRangeUnits, attackDamage, attackCooldownTicks, aggroScanInterval, fleeHealthPct);

    // Assert the body CENTRE is collision-valid: at least `radius` (minus a tiny tolerance) from every blocked tile's
    // 1x1 AABB — the resolver's invariant (a move must never land the circle penetrating a wall). Mirrors the BasicRoamer
    // tests' helper so a flee move is held to the same collision contract as a chase/roam move.
    private static void AssertCollisionValid(WorldVector pos, IReadOnlySet<TileCoord> blocked, string where)
    {
        const double tol = 1e-6;
        foreach (var tile in blocked)
        {
            var cx = System.Math.Clamp(pos.X, tile.X - 0.5d, tile.X + 0.5d);
            var cy = System.Math.Clamp(pos.Y, tile.Y - 0.5d, tile.Y + 0.5d);
            var d = Distance(pos, new WorldVector(cx, cy));
            Assert.True(d >= BodyRadius - tol,
                $"{where}: body centre {pos} penetrates blocked tile {tile} (dist {d:F4} < radius {BodyRadius}).");
        }
    }

    [Fact]
    public void WoundedSkirmisher_FleesAwayFromTarget_AndDoesNotAttack()
    {
        // Below the flee threshold (HP 20 <= 0.3*100): the skirmisher aggros, then GLIDES AWAY from the player instead
        // of approaching/attacking. Distance to the target grows, its position moves OPPOSITE the target, and the
        // attack delegate is never called.
        var grid = OpenGrid();
        var world = new WorldState();
        var home = WorldVector.FromTile(new TileCoord(32, 32));
        var monster = SpawnMonster(world, home.ToTileRounded(), health: 20, maxHealth: 100);
        var player = world.AddTransient(2, EntityKind.Player, "Hero", new TileCoord(34, 32), Direction8.S);
        var hits = new int[1];
        var behavior = CreateBehavior(seed: 7, grid, world, player, hits, skirmisher: true);
        var glide = CreateGlide(grid, world);
        behavior.Register(monster, serverTick: 0, pauseMinTicks: 100, pauseMaxTicks: 100, aggroScanIntervalTicks: 1);

        var startDist = Distance(monster.Position, player.Position);
        for (uint tick = 1; tick <= 30; tick++)
        {
            behavior.StepMonster(monster, tick, StepCooldownTicks, Tunables(fleeHealthPct: 0.3d), glide);
        }

        Assert.True(behavior.TryGetPhase(monster.Id, out var phase) && phase == BasicRoamerBehavior.State.Chasing,
            "a wounded skirmisher should still be in Chasing while it flees (big de-aggro/leash).");
        Assert.True(monster.Position.X < home.X - 0.5d,
            $"skirmisher did not flee WEST away from the eastern player (x={monster.Position.X:F3}).");
        Assert.True(Distance(monster.Position, player.Position) > startDist + 0.5d,
            "skirmisher did not increase its distance from the target while fleeing.");
        Assert.Equal(0, hits[0]); // it never attacked while fleeing.
    }

    [Fact]
    public void HealthySkirmisher_ChasesAndAttacks_IdenticallyToABasicRoamer()
    {
        // At/above the threshold (full HP) the flee override is INERT: a healthy skirmisher must behave byte-identically
        // to a BasicRoamer. Run both with the same seed/world/setup and assert identical position paths + identical hits
        // (the chase converges + attacks at range). This is the strongest "inert above the threshold" proof.
        static (List<WorldVector> path, int hits) Run(bool skirmisher)
        {
            var grid = OpenGrid();
            var world = new WorldState();
            var monster = SpawnMonster(world, new TileCoord(32, 32), health: 100, maxHealth: 100);
            var player = world.AddTransient(2, EntityKind.Player, "Hero", new TileCoord(38, 32), Direction8.S);
            var hits = new int[1];
            var behavior = CreateBehavior(seed: 7, grid, world, player, hits, skirmisher);
            var glide = CreateGlide(grid, world);
            behavior.Register(monster, serverTick: 0, pauseMinTicks: 100, pauseMaxTicks: 100, aggroScanIntervalTicks: 1);

            var path = new List<WorldVector>();
            for (uint tick = 1; tick <= 80; tick++)
            {
                behavior.StepMonster(monster, tick, StepCooldownTicks, Tunables(fleeHealthPct: 0.3d), glide);
                path.Add(monster.Position);
            }

            return (path, hits[0]);
        }

        var (skirmPath, skirmHits) = Run(skirmisher: true);
        var (basicPath, basicHits) = Run(skirmisher: false);

        Assert.Equal(basicPath, skirmPath);   // identical movement — the override never fired above the threshold.
        Assert.Equal(basicHits, skirmHits);
        Assert.True(skirmHits > 0, "a healthy skirmisher should close to range and attack (like a basic roamer).");
    }

    [Fact]
    public void FleeHealthPctZero_NeverFlees_EvenAtOneHp()
    {
        // FleeHealthPct 0 (the default — never flee). Even at 1 HP the skirmisher behaves as a basic roamer: it chases
        // an adjacent player and ATTACKS rather than running away (it does not glide off home).
        var grid = OpenGrid();
        var world = new WorldState();
        var home = WorldVector.FromTile(new TileCoord(32, 32));
        var monster = SpawnMonster(world, home.ToTileRounded(), health: 1, maxHealth: 100);
        var player = world.AddTransient(2, EntityKind.Player, "Hero", new TileCoord(33, 32), Direction8.S);
        var hits = new int[1];
        var behavior = CreateBehavior(seed: 7, grid, world, player, hits, skirmisher: true);
        var glide = CreateGlide(grid, world);
        behavior.Register(monster, serverTick: 0, pauseMinTicks: 100, pauseMaxTicks: 100, aggroScanIntervalTicks: 1);

        for (uint tick = 1; tick <= 100; tick++)
        {
            behavior.StepMonster(monster, tick, StepCooldownTicks, Tunables(fleeHealthPct: 0d, attackCooldownTicks: 20), glide);
        }

        Assert.True(hits[0] > 0, "with FleeHealthPct 0 the 1-HP skirmisher should attack, not flee.");
        Assert.True(Distance(monster.Position, home) < 0.5d,
            $"skirmisher fled despite FleeHealthPct 0 (moved {Distance(monster.Position, home):F3} from home).");
    }

    [Fact]
    public void WoundedSkirmisher_FleeMoveIsCollisionValid_StopsAtWall_NeverPenetrates()
    {
        // The flee heading is away from the target AND the move is collision-valid. A wall sits to the WEST of the
        // monster (the flee direction, since the player is to the EAST). The skirmisher glides west to flee, and the
        // resolver STOPS it at the wall — it moves away from the player but its body centre never penetrates the wall.
        var wall = new TileCoord(30, 32);
        var grid = new TileGrid(GridSize, GridSize, new[] { wall });
        var world = new WorldState();
        var home = WorldVector.FromTile(new TileCoord(32, 32));
        var monster = SpawnMonster(world, home.ToTileRounded(), health: 20, maxHealth: 100);
        var player = world.AddTransient(2, EntityKind.Player, "Hero", new TileCoord(34, 32), Direction8.S);
        var hits = new int[1];
        var behavior = CreateBehavior(seed: 7, grid, world, player, hits, skirmisher: true);
        var glide = CreateGlide(grid, world);
        behavior.Register(monster, serverTick: 0, pauseMinTicks: 100, pauseMaxTicks: 100, aggroScanIntervalTicks: 1);

        var minX = monster.Position.X;
        for (uint tick = 1; tick <= 60; tick++)
        {
            behavior.StepMonster(monster, tick, StepCooldownTicks, Tunables(fleeHealthPct: 0.3d), glide);
            AssertCollisionValid(monster.Position, grid.BlockedTiles, $"tick {tick}");
            minX = System.Math.Min(minX, monster.Position.X);
        }

        // It fled WEST (toward the wall / away from the eastern player) ...
        Assert.True(minX < home.X - 0.5d, $"skirmisher never fled west toward the wall (minX={minX:F3}).");
        // ... but the resolver stopped it before the body centre could penetrate the wall AABB (centre.x >= 31.0).
        Assert.True(minX >= 31.0d - 1e-6, $"skirmisher penetrated the wall while fleeing (minX={minX:F3} < 31.0).");
        Assert.Equal(0, hits[0]); // never attacked — it was fleeing the whole time.
    }
}
