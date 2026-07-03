using Mmo.Server.Runtime;
using Mmo.Shared.Domain;
using Mmo.Shared.Domain.Actions;
using Xunit;

namespace Mmo.Server.Tests;

// LIVING-ENEMIES P1/P2 + CONTINUOUS MIGRATION (Phase 8): headless tests for the monster brain (BasicRoamerBehavior), driven
// directly against a WorldState + TileGrid + the real HopLocomotion + injected target/attack callbacks (no
// network/GameServer). Randomness is seeded so every assertion is deterministic. These pin the CONTINUOUS-NAVIGATION /
// HOP contract the human live-verifies: a monster is still most of the time, occasionally HOPS within its Euclidean
// leash, AGGROS the nearest player in the Euclidean aggro disc, CHASES (Euclidean-leashed to home), ATTACKS when
// within AttackRangeUnits on its own cooldown, de-aggros at the Euclidean de-aggro/leash ranges, NEVER lands a hop
// inside a wall (the collision-valid headline), SOME hops land sub-tile (proving continuous nav), and never freezes
// against a wall the resolver slides to a fixpoint (the re-based livelock watchdog).
public sealed class BasicRoamerBehaviorTests
{
    // A generous open grid so the leash is the only thing bounding the wander for the leash/cadence tests. Walls are
    // introduced explicitly in the collision-valid / wedge tests.
    private const int GridSize = 64;
    private const uint StepCooldownTicks = 3;        // the project's base cadence.
    private const double BodyRadius = 0.5d;          // the player body radius the monster also collides at.
    private const double HopDistance = 1.0d;         // one tile per hop (the default slime knob).
    private const double HopHeightUnits = 0.5d;      // the slime's real ballistic apex (the default type knob).
    private const int TickRate = 20;                 // server tick rate (fixes the ballistic constants; XY is rate-free).

    private static TileGrid OpenGrid() => new(GridSize, GridSize, []);

    // MOVEMENT-ACTIONS (Phase C): the hop is now a REAL ballistic Jump driven by the shared ServerActionExecutor — the
    // HopLocomotion only DECIDES + STARTS the hop; the executor advances the arc per tick. This harness bundles the AI +
    // its executor + the world and exposes a one-call StepMonster that mirrors GameServer's tick order (StepMonsterAi
    // THEN StepAll), so EVERY existing assertion (collision-valid landings, cadence, leash, livelock) keeps its exact
    // meaning against the real refactored path. The passthroughs keep the test bodies unchanged (still call StepMonster /
    // TryGetPhase / etc. on `ai`). NOTE: we drive the real executor here rather than a fake instant-landing locomotion
    // (both are allowed by the spec) so the collision-valid / wedge / sub-tile tests exercise the real per-tick arc.
    private sealed class AiHarness
    {
        private readonly BasicRoamerBehavior _ai;
        private readonly IMonsterLocomotion _locomotion;
        private readonly ServerActionExecutor _executor;
        private readonly WorldState _world;

        // MONSTER-BEHAVIOR P1: the locomotion is now passed PER STEP (GameServer resolves it per-type each tick), so
        // the harness holds the one HopLocomotion and threads it into StepMonster — the test bodies stay unchanged.
        public AiHarness(BasicRoamerBehavior ai, IMonsterLocomotion locomotion, ServerActionExecutor executor, WorldState world)
        {
            _ai = ai;
            _locomotion = locomotion;
            _executor = executor;
            _world = world;
        }

        public int TrackedCount => _ai.TrackedCount;

        // SLAM-REVIEW-FOLLOWUPS item 3 (the deferred-start retry pin): exposes the harness's real executor so a
        // test can manually occupy it with an unrelated in-flight action BEFORE driving any AI ticks — the
        // "contrived def" the todo calls for, which forces BeginSlamLeapDelegate's own TryStart to decline on
        // every retry until the blocker frees. A plain read-only passthrough; nothing else about the harness changes.
        public ServerActionExecutor Executor => _executor;

        public void Register(WorldEntity monster, uint serverTick, uint pauseMinTicks, uint pauseMaxTicks, uint aggroScanIntervalTicks)
            => _ai.Register(monster, serverTick, pauseMinTicks, pauseMaxTicks, aggroScanIntervalTicks);

        // One tick in GameServer order: the AI decides + STARTS a hop (StepMonster, told its per-step locomotion), then
        // the executor advances every in-flight arc (StepAll). Returns the AI's moved flag — true on the tick a hop
        // STARTS (the same Moved cadence the pre-refactor instant hop reported: one per cadence window).
        public bool StepMonster(WorldEntity monster, uint serverTick, uint stepCooldownTicks, in MonsterAiTunables t)
        {
            var moved = _ai.StepMonster(monster, serverTick, stepCooldownTicks, t, _locomotion);
            _executor.StepAll(_world, serverTick);
            return moved;
        }

        public bool TryGetPhase(ulong monsterId, out BasicRoamerBehavior.State phase) => _ai.TryGetPhase(monsterId, out phase);

        public bool TryGetHome(ulong monsterId, out WorldVector home) => _ai.TryGetHome(monsterId, out home);

        public bool TryGetTarget(ulong monsterId, out ulong targetId) => _ai.TryGetTarget(monsterId, out targetId);
    }

    // Builds the action executor wired exactly like GameServer: the SAME shared wall query + body radius ordinary
    // movement/the hop use, and the SAME apply seam (ApplyResolvedMove + spatial-bucket migration on a tile cross).
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

    // Builds a HopLocomotion whose begin-hop starts a REAL ballistic Jump on `executor` (mirroring GameServer.BeginMonster
    // Hop): a per-hop ForwardArc def spanning the whole cadence, jump height = HopHeightUnits, cooldown 0 (the AI's
    // TryBeginHop is the gate), and the IsActive gate reads the executor so the AI can't re-hop mid-arc.
    private static HopLocomotion CreateLocomotion(TileGrid grid, ServerActionExecutor executor)
        => new(
            () => HopDistance,
            () => BodyRadius,
            grid.QueryNearbyWalls,
            (monster, heading, hopDistance, cooldownTicks, serverTick) =>
            {
                var def = MovementActionRegistry.BuildForwardArcJump(
                    ActionId.Jump,
                    durationTicks: cooldownTicks,
                    jumpHeight: HopHeightUnits,
                    forwardDistanceUnits: hopDistance,
                    cooldownTicks: 0,
                    animationId: 1);
                return executor.TryStart(monster, def, heading, serverTick);
            },
            id => executor.IsActive(id));

    // Roam-only tunables (no aggro: AggroRadius 0 disables the scan). Euclidean float ranges.
    private static MonsterAiTunables RoamTunables(double roamRadius, uint pauseMin, uint pauseMax)
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
    // MONSTER-BEHAVIOR P2: a continuous-walk GlideLocomotion wired like GameServer — same shared wall query + body
    // radius + apply seam ordinary movement uses, dt fixed by the tick rate. The glider reads SpeedUnitsPerSecond off
    // the entity (the test seeds it), SETS Velocity = heading×speed, and moves every tick (no executor — it applies
    // directly), so the AI's roam/chase/leash/watchdog logic must drive a walker with no change.
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

    private static AiHarness CreateAi(
        int seed,
        TileGrid grid,
        WorldState world,
        BasicRoamerBehavior.FindTargetDelegate? findTarget = null,
        BasicRoamerBehavior.TryResolveTargetDelegate? tryResolve = null,
        BasicRoamerBehavior.AttackDelegate? attack = null,
        Func<TileGrid, ServerActionExecutor, IMonsterLocomotion>? locomotionFactory = null,
        BasicRoamerBehavior.TrySlamDelegate? trySlam = null,
        Func<ServerActionExecutor, BasicRoamerBehavior.BeginSlamLeapDelegate>? beginSlamLeapFactory = null)
    {
        var executor = CreateExecutor(grid, world);
        var locomotion = (locomotionFactory ?? ((g, e) => CreateLocomotion(g, e)))(grid, executor);
        var ai = new BasicRoamerBehavior(
            seed,
            grid.IsWalkable,
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
            attack ?? ((WorldEntity _, ulong _, int _) => { }),
            // TELEGRAPH T1: the optional slam trigger (null = never slam, exactly like GameServer for a
            // non-slammer type); the slam-cadence test records + accepts casts through it.
            trySlam: trySlam,
            // SLIME-SLAM ROOT+LEAP: the optional slam-leap dep (a factory over the harness executor so the test
            // leap starts real arcs on the SAME executor the hop uses, mirroring GameServer.BeginMonsterSlamLeap).
            // Null = the channel roots but never leaps (the brain's documented degenerate default).
            beginSlamLeap: beginSlamLeapFactory?.Invoke(executor));
        return new AiHarness(ai, locomotion, executor, world);
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
        Assert.True(ai.TryGetPhase(monster.Id, out var phase) && phase == BasicRoamerBehavior.State.Idle);
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
            Assert.True(ai.TryGetPhase(monster.Id, out var phase) && phase == BasicRoamerBehavior.State.Idle,
                $"expected Idle before pause elapses (tick {tick}).");
        }

        ai.StepMonster(monster, pause, StepCooldownTicks, RoamTunables(roamRadius, pause, pause));
        Assert.True(ai.TryGetPhase(monster.Id, out var roamingPhase));
        Assert.Equal(BasicRoamerBehavior.State.Roaming, roamingPhase);

        var sawIdleAgain = false;
        for (uint tick = pause + 1; tick <= pause + 400; tick++)
        {
            ai.StepMonster(monster, tick, StepCooldownTicks, RoamTunables(roamRadius, pause, pause));
            if (ai.TryGetPhase(monster.Id, out var p) && p == BasicRoamerBehavior.State.Idle)
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

    private static MonsterAiTunables CombatTunables(
        double roamRadius = 4d,
        uint pauseMin = 100,
        uint pauseMax = 100,
        double aggroRadius = 6d,
        double deaggroRadius = 9d,
        double chaseLeash = 12d,
        double attackRangeUnits = 1.5d,
        int attackDamage = 10,
        uint attackCooldownTicks = 20,
        uint aggroScanInterval = 1,
        bool slamEnabled = false,
        uint slamCooldownTicks = 0)
        => new(
            roamRadius, pauseMin, pauseMax,
            aggroRadius, deaggroRadius, chaseLeash,
            attackRangeUnits, attackDamage, attackCooldownTicks, aggroScanInterval)
        {
            // TELEGRAPH T1: slam config defaults to inert (the pre-T1 brain); the slam-cadence test opts in.
            SlamEnabled = slamEnabled,
            SlamCooldownTicks = slamCooldownTicks,
        };

    // Wires the AI's aggro/resolve/attack callbacks to a real player WorldEntity, mirroring GameServer's continuous
    // path: findTarget — nearest alive player by Euclidean Position within the COARSE gather radius (the AI re-tests
    // the Euclidean aggro disc); tryResolve — the target's live Position + alive; attack — face + ApplyDamage.
    private static AiHarness CreateCombatAi(
        int seed, TileGrid grid, WorldState world, WorldEntity player, int[] hitCounter,
        BasicRoamerBehavior.TrySlamDelegate? trySlam = null,
        Func<ServerActionExecutor, BasicRoamerBehavior.BeginSlamLeapDelegate>? beginSlamLeapFactory = null)
    {
        return CreateAi(
            seed, grid, world,
            trySlam: trySlam,
            beginSlamLeapFactory: beginSlamLeapFactory,
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
        Assert.Equal(BasicRoamerBehavior.State.Chasing, phase);
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
            Assert.NotEqual(BasicRoamerBehavior.State.Chasing, phase);
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
        // Player 5 tiles east = Euclidean 5.0, COMFORTABLY inside the 6.0 aggro radius (not AT the 6.0 boundary — a
        // boundary setup relied on the aggro test being inclusive `<=`; 5.0 aggros unambiguously so this can't fall
        // back to RNG roam if the comparison ever tightens).
        var player = world.AddTransient(2, EntityKind.Player, "Hero", new TileCoord(37, 32), Direction8.S);
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
    public void SlamCadence_AdjacentTarget_CastsExactlyOncePerCooldownWindow()
    {
        // TELEGRAPH T1 review followup: the brain-level slam-CADENCE pin. A slam-enabled monster with an adjacent
        // (in-attack-range) target must cast exactly ⌈N/cooldown⌉ slams over N ticks — spaced by the per-monster
        // NextSlamTick re-arm in BasicRoamerBehavior.StepChase (the brain OWNS this clock; a slam is a scheduled
        // world event, so there is no executor cooldown behind it the way the charge has). Delete that re-arm and
        // an in-range slammer casts EVERY tick (live: 20 casts/s, 15 dmg at every T, the scheduler's _pending
        // ballooning) while the rest of the suite stays green — the exact-tick assert below is what catches it.
        const uint slamCooldown = 20;
        const uint ticks = 100;
        var grid = OpenGrid();
        var world = new WorldState();
        var monster = SpawnMonster(world, new TileCoord(32, 32), networkId: 1);
        var player = world.AddTransient(2, EntityKind.Player, "Hero", new TileCoord(33, 32), Direction8.S);
        var hits = new int[1];
        var slamCastTicks = new List<uint>();
        var ai = CreateCombatAi(
            seed: 7, grid, world, player, hits,
            trySlam: (WorldEntity _, ulong targetId, WorldVector targetPos, uint tick, out SlamCast cast) =>
            {
                Assert.Equal(player.Id, targetId);
                slamCastTicks.Add(tick);
                // SLIME-SLAM ROOT+LEAP: a realistic plan (windup 10 < the 20-tick cooldown; a 4-tick leap window)
                // so the brain runs the ROOT+LEAP channel between casts exactly like live — the cadence pin below
                // must hold WITH the channel in the loop. No leap dep is wired here, so the channel roots in place
                // (the leap landing has its own dedicated pins); this test pins the CAST-tick arithmetic only.
                cast = new SlamCast(targetPos, LeapStartTick: tick + 7, ResolveTick: tick + 10);
                return true; // cast accepted — GameServer's TryBeginMonsterSlam would schedule the telegraph here.
            });
        ai.Register(monster, serverTick: 0, pauseMinTicks: 100, pauseMaxTicks: 100, aggroScanIntervalTicks: 1);

        // attackDamage 0 keeps the interleaved melee (its own independent timer) from ever downing the target.
        var tunables = CombatTunables(attackDamage: 0, slamEnabled: true, slamCooldownTicks: slamCooldown);
        for (uint tick = 1; tick <= ticks; tick++)
        {
            ai.StepMonster(monster, tick, StepCooldownTicks, tunables);
        }

        // Adjacent (Euclidean 1.0 <= 1.5) for the whole run and registered with NextSlamTick = 0, so the first cast
        // fires on tick 1 and each subsequent one exactly one cooldown later: ticks 1,21,41,61,81 — ⌈100/20⌉ = 5
        // casts, the same cadence arithmetic as the melee test above. Any extra entry means the re-arm is gone.
        // The re-arm anchors at the CAST tick and the channel (10 ticks) ends well inside the cooldown window, so
        // the root-and-leap redesign leaves these exact ticks untouched.
        Assert.Equal(new uint[] { 1, 21, 41, 61, 81 }, slamCastTicks);
        Assert.Equal((int)Math.Ceiling(ticks / (double)slamCooldown), slamCastTicks.Count);
    }

    // ---------------------------------------------------------------------------------------------------------
    // SLIME-SLAM ROOT+LEAP (todo/S-slime-slam-root-and-leap.md): rooted channel + leap-to-locked-origin pins.
    // ---------------------------------------------------------------------------------------------------------

    // The test slam's timing knobs, mirroring the live derivation (slime: windup 1500 ms = 30 ticks @20 Hz, hop
    // airborne 300 ms = 6 ticks): windup 20 / leap 4 keeps the same shape (leap ≪ windup) with short test runs.
    private const uint SlamWindupTicks = 20;
    private const uint SlamLeapTicks = 4;

    // A trySlam fake producing GameServer's exact cast plan: origin locked at the target's CAST-time position and
    // leapStart = resolve − leap + 1, so the arc (first-stepped the same tick it starts — the harness mirrors the
    // GameServer StepMonsterAi→StepAll order) lands EXACTLY on the resolve tick. Records the cast tick + origin.
    private static BasicRoamerBehavior.TrySlamDelegate PlanSlam(uint[] castTick, WorldVector[] origin)
        => (WorldEntity _, ulong _, WorldVector targetPos, uint tick, out SlamCast cast) =>
        {
            castTick[0] = tick;
            origin[0] = targetPos;
            cast = new SlamCast(targetPos, LeapStartTick: tick + SlamWindupTicks - SlamLeapTicks + 1, ResolveTick: tick + SlamWindupTicks);
            return true;
        };

    // The harness's slam-leap dep, mirroring GameServer.BeginMonsterSlamLeap: a real ballistic Jump on the SAME
    // executor the hop uses (same ActionId/height/animation — the leap replicates as a hop), duration recomputed
    // from the ticks remaining so a deferred start shortens toward the deadline, cadence armed AFTER the start.
    private static BasicRoamerBehavior.BeginSlamLeapDelegate CreateSlamLeap(ServerActionExecutor executor)
        => (monster, origin, resolveTick, serverTick) =>
        {
            var toOrigin = origin - monster.Position;
            var remaining = resolveTick >= serverTick ? resolveTick - serverTick + 1u : 1u;
            var duration = Math.Max(1u, Math.Min(SlamLeapTicks, remaining));
            var def = MovementActionRegistry.BuildForwardArcJump(
                ActionId.Jump,
                durationTicks: duration,
                jumpHeight: HopHeightUnits,
                forwardDistanceUnits: toOrigin.Length,
                cooldownTicks: 0,
                animationId: 1);
            var heading = toOrigin.LengthSquared > 0d ? toOrigin.Normalized() : WorldVector.Zero;
            if (!executor.TryStart(monster, def, heading, serverTick))
            {
                return false;
            }

            monster.TryBeginHop(serverTick, duration); // begin first, then arm — the frozen/ready complement rule.
            return true;
        };

    [Fact]
    public void SlamChannel_RootsFromCastToResolve_NoMeleeNoMovement_ThenLeapLandsOnResolve()
    {
        // THE ROOT+LEAP HEADLINE. An adjacent target triggers a slam cast; from cast to resolve the slime is a
        // COMMITTED channel: (a) not a hair of movement until the leap-start tick (rooted — no hops, no chasing),
        // (b) ZERO melee swings for the whole windup (the melee timer was due the entire time — only the channel
        // suppresses it), then (c) the leap arc lands the slime ON the locked origin exactly at the resolve tick,
        // grounded, back in Chasing, and (d) the root has RELEASED (melee resumes immediately after).
        var grid = OpenGrid();
        var world = new WorldState();
        var monster = SpawnMonster(world, new TileCoord(32, 32), networkId: 1);
        var player = world.AddTransient(2, EntityKind.Player, "Hero", new TileCoord(33, 32), Direction8.S);
        var hits = new int[1];
        var castTick = new uint[1];
        var origin = new WorldVector[1];
        var ai = CreateCombatAi(
            seed: 7, grid, world, player, hits,
            trySlam: PlanSlam(castTick, origin),
            beginSlamLeapFactory: CreateSlamLeap);
        ai.Register(monster, serverTick: 0, pauseMinTicks: 100, pauseMaxTicks: 100, aggroScanIntervalTicks: 1);
        // Cooldown longer than the run so exactly ONE cast happens; melee damage 10 so a suppressed-swing leak
        // would show up in hits[0] immediately.
        var tunables = CombatTunables(attackDamage: 10, slamEnabled: true, slamCooldownTicks: 500);

        // Tick 1: adjacent (Euclidean 1.0 <= 1.5) + NextSlamTick seeded at spawn → the cast fires and the channel
        // starts THIS tick (the slam takes the tick; melee did not fire).
        ai.StepMonster(monster, 1, StepCooldownTicks, tunables);
        Assert.Equal(1u, castTick[0]);
        Assert.True(ai.TryGetPhase(monster.Id, out var channeling));
        Assert.Equal(BasicRoamerBehavior.State.SlamChanneling, channeling);

        var castPos = monster.Position;
        var resolveTick = castTick[0] + SlamWindupTicks;              // 21
        var leapStartTick = resolveTick - SlamLeapTicks + 1;          // 18 — the arc's 4 steps land ON 21.
        for (uint tick = 2; tick <= resolveTick; tick++)
        {
            ai.StepMonster(monster, tick, StepCooldownTicks, tunables);
            Assert.Equal(0, hits[0]); // (b) no melee between cast and resolve — ever.
            if (tick < leapStartTick)
            {
                // (a) ROOTED: byte-identical position until the leap fires.
                Assert.Equal(castPos, monster.Position);
            }
        }

        // (c) Landed ON the resolve tick at the LOCKED origin (the target's cast-time position), grounded, channel
        // over. The tolerance is float-sum slack only — the arc is 4 exact quarter-steps on an open grid.
        Assert.True(Distance(monster.Position, origin[0]) <= 1e-3,
            $"leap landed {monster.Position}, expected the locked origin {origin[0]}.");
        Assert.Equal(0d, monster.VerticalOffset, 1e-9);
        Assert.True(ai.TryGetPhase(monster.Id, out var after));
        Assert.Equal(BasicRoamerBehavior.State.Chasing, after);

        // (d) The root released with the channel: the (never-fired) melee timer swings within a few ticks.
        for (uint tick = resolveTick + 1; tick <= resolveTick + 5 && hits[0] == 0; tick++)
        {
            ai.StepMonster(monster, tick, StepCooldownTicks, tunables);
        }

        Assert.True(hits[0] > 0, "melee never resumed after the channel — the root leaked past the resolve tick.");
    }

    [Fact]
    public void SlamLeap_LandsOnTheLockedOrigin_NotTheDodgedTarget()
    {
        // The commit-to-where-you-WERE fantasy: the origin locks at cast; the target dodges mid-windup; the slime
        // still leaps to (and lands on) the LOCKED origin — never re-aims at the target's new position — and lands
        // nowhere near the dodger.
        var grid = OpenGrid();
        var world = new WorldState();
        var monster = SpawnMonster(world, new TileCoord(32, 32), networkId: 1);
        var player = world.AddTransient(2, EntityKind.Player, "Hero", new TileCoord(33, 32), Direction8.S);
        var hits = new int[1];
        var castTick = new uint[1];
        var origin = new WorldVector[1];
        var ai = CreateCombatAi(
            seed: 7, grid, world, player, hits,
            trySlam: PlanSlam(castTick, origin),
            beginSlamLeapFactory: CreateSlamLeap);
        ai.Register(monster, serverTick: 0, pauseMinTicks: 100, pauseMaxTicks: 100, aggroScanIntervalTicks: 1);
        var tunables = CombatTunables(attackDamage: 10, slamEnabled: true, slamCooldownTicks: 500);

        ai.StepMonster(monster, 1, StepCooldownTicks, tunables);
        Assert.Equal(1u, castTick[0]);
        var resolveTick = castTick[0] + SlamWindupTicks;

        for (uint tick = 2; tick <= resolveTick; tick++)
        {
            if (tick == 10)
            {
                // DODGE: step well out of the (locked) zone mid-windup — still inside de-aggro range (9).
                var before = player.TileCoord;
                player.TeleportTo(new TileCoord(38, 32));
                world.OnEntityMoved(player, before);
            }

            ai.StepMonster(monster, tick, StepCooldownTicks, tunables);
        }

        // Landed on the LOCKED origin — the dodger's new position played no part in the leap.
        Assert.True(Distance(monster.Position, origin[0]) <= 1e-3,
            $"leap landed {monster.Position}, expected the locked origin {origin[0]}.");
        Assert.True(Distance(monster.Position, player.Position) > 3d,
            "the slime ended up at the dodged target — the leap must aim at the locked origin, not track the target.");
        Assert.Equal(0, hits[0]); // and no melee ever fired inside the channel.
    }

    [Fact]
    public void SlamLeap_StillAirborneOneTickBeforeResolve_ThenGroundedExactlyOnResolveTick()
    {
        // SLAM-REVIEW-FOLLOWUPS item 2 (the two-sided leap-landing pin). SlamChannel_RootsFromCastToResolve_...
        // above only checks the LATE side — its assertions on the landed position all run AT OR AFTER the resolve
        // tick, so an off-by-one that lands the arc a tick EARLY (e.g. flipping `leapStartTick = resolveTick −
        // leapDurationTicks + 1` to `... - leapDurationTicks` — dropping the "+1") would already show a grounded,
        // on-origin slime by the time that test's loop reaches the resolve tick, and it would stay GREEN. This
        // test adds the missing EARLY-side check: one tick before the resolve tick, the slime must STILL be
        // mid-arc (VerticalOffset > 0, not yet at the origin) — an early landing zeroes VerticalOffset (and is
        // already at/near the origin) a tick sooner than this, which is exactly what fails here.
        var grid = OpenGrid();
        var world = new WorldState();
        var monster = SpawnMonster(world, new TileCoord(32, 32), networkId: 1);
        var player = world.AddTransient(2, EntityKind.Player, "Hero", new TileCoord(33, 32), Direction8.S);
        var hits = new int[1];
        var castTick = new uint[1];
        var origin = new WorldVector[1];
        var ai = CreateCombatAi(
            seed: 7, grid, world, player, hits,
            trySlam: PlanSlam(castTick, origin),
            beginSlamLeapFactory: CreateSlamLeap);
        ai.Register(monster, serverTick: 0, pauseMinTicks: 100, pauseMaxTicks: 100, aggroScanIntervalTicks: 1);
        var tunables = CombatTunables(attackDamage: 10, slamEnabled: true, slamCooldownTicks: 500);

        ai.StepMonster(monster, 1, StepCooldownTicks, tunables);
        Assert.Equal(1u, castTick[0]);
        var resolveTick = castTick[0] + SlamWindupTicks; // 21 — same shipped-shape numbers as the headline test.

        for (uint tick = 2; tick < resolveTick; tick++)
        {
            ai.StepMonster(monster, tick, StepCooldownTicks, tunables);
        }

        // ONE TICK BEFORE resolve (tick 20): still mid-arc, not yet at the locked origin. An EARLY-landing
        // regression would already show VerticalOffset == 0 and Position == origin here.
        Assert.True(monster.VerticalOffset > 0d,
            $"expected the slime still airborne at tick {resolveTick - 1} (one tick before resolve); " +
                "VerticalOffset was 0 — the leap landed EARLY.");
        Assert.True(Distance(monster.Position, origin[0]) > 1e-3,
            "expected the slime NOT yet at the locked origin one tick before resolve — the leap landed EARLY.");

        ai.StepMonster(monster, resolveTick, StepCooldownTicks, tunables);

        // ON the resolve tick: grounded, exactly at the locked origin — the LATE side, re-confirmed here so this
        // test is a complete, self-contained two-sided pin (not dependent on the headline test also passing).
        Assert.Equal(0d, monster.VerticalOffset, 1e-9);
        Assert.True(Distance(monster.Position, origin[0]) <= 1e-3,
            $"leap landed {monster.Position}, expected the locked origin {origin[0]} exactly on the resolve tick.");
    }

    [Fact]
    public void SlamLeap_DeferredStart_ExecutorBusyPastThePlannedLeapStartTick_StillLandsExactlyOnResolveTick()
    {
        // SLAM-REVIEW-FOLLOWUPS item 3 (the deferred-start retry pin). StepSlamChannel retries _beginSlamLeap
        // every tick from LeapStartTick until the executor accepts (GameServer's own comment: "an in-flight hop
        // arc from just before the cast, or a pre-armed longer movement cadence the root's max-floor could not
        // shorten, can defer the accept"). That retry path is UNREACHABLE with the live slime numbers (the
        // GROUNDED gate in GameServer.TryBeginMonsterSlam refuses to even CAST while an action is active, and the
        // channel's own root blocks the AI from starting a competing hop mid-windup) — so nothing in the live game
        // currently drives the executor busy at the planned leap-start tick. Pin it directly: manually occupy the
        // executor with an unrelated in-place "stall" jump (the "contrived def" the todo calls for) that outlives
        // the planned LeapStartTick by one tick, so BeginSlamLeapDelegate's TryStart declines on every retry until
        // the blocker frees — and assert the EVENTUAL (1-tick-late-starting) leap recomputes its duration from the
        // ticks REMAINING (`resolveTick - serverTick + 1`, per GameServer.BeginMonsterSlamLeap's own doc comment)
        // and still lands EXACTLY on the resolve tick. Hard-coding the original (undeferred) leap duration instead
        // of recomputing it from the deferred start would land this test ONE TICK LATE.
        var grid = OpenGrid();
        var world = new WorldState();
        var monster = SpawnMonster(world, new TileCoord(32, 32), networkId: 1);
        var player = world.AddTransient(2, EntityKind.Player, "Hero", new TileCoord(33, 32), Direction8.S);
        var hits = new int[1];
        var castTick = new uint[1];
        var origin = new WorldVector[1];
        var ai = CreateCombatAi(
            seed: 7, grid, world, player, hits,
            trySlam: PlanSlam(castTick, origin),
            beginSlamLeapFactory: CreateSlamLeap);
        ai.Register(monster, serverTick: 0, pauseMinTicks: 100, pauseMaxTicks: 100, aggroScanIntervalTicks: 1);
        var tunables = CombatTunables(attackDamage: 10, slamEnabled: true, slamCooldownTicks: 500);

        // Occupy the executor BEFORE any AI tick runs, with a zero-forward-distance "stall" jump (isolates this
        // pin to the retry/duration math, not a position side effect) active through harness tick 18 (frees at
        // 19) — one tick PAST the plan's own LeapStartTick (18 = resolveTick(21) - SlamLeapTicks(4) + 1). PlanSlam
        // doesn't gate on executor state (that's GameServer.TryBeginMonsterSlam's separate grounded gate, pinned
        // elsewhere), so the cast itself still fires on schedule — only the LEAP's own TryStart is affected.
        var blocker = MovementActionRegistry.BuildForwardArcJump(
            ActionId.Jump, durationTicks: 18, jumpHeight: 0d, forwardDistanceUnits: 0d, cooldownTicks: 0, animationId: 1);
        Assert.True(ai.Executor.TryStart(monster, blocker, WorldVector.Zero, serverTick: 1));

        ai.StepMonster(monster, 1, StepCooldownTicks, tunables);
        Assert.Equal(1u, castTick[0]);
        var resolveTick = castTick[0] + SlamWindupTicks;         // 21
        var plannedLeapStart = resolveTick - SlamLeapTicks + 1;  // 18

        for (uint tick = 2; tick < plannedLeapStart; tick++)
        {
            ai.StepMonster(monster, tick, StepCooldownTicks, tunables);
            Assert.True(ai.TryGetPhase(monster.Id, out var phase) && phase == BasicRoamerBehavior.State.SlamChanneling,
                $"expected still channeling (blocker occupying the executor) at tick {tick}.");
        }

        // Confirm the blocker is still active going into the planned leap-start tick — the setup this test relies
        // on (the retry about to be attempted, and about to be declined).
        Assert.True(ai.Executor.IsActive(monster.Id), "test setup: the blocker should still be occupying the executor.");

        // Tick 18: the retry is attempted and DECLINED (blocker still active) — still channeling, not yet leaping.
        ai.StepMonster(monster, plannedLeapStart, StepCooldownTicks, tunables);
        Assert.True(ai.TryGetPhase(monster.Id, out var stillChanneling) && stillChanneling == BasicRoamerBehavior.State.SlamChanneling);

        // Tick 19: the blocker frees (its 18-tick duration ends during tick 18's step); THIS retry succeeds — the
        // leap starts ONE TICK LATE. Its duration must recompute from the ticks remaining
        // (min(SlamLeapTicks, resolveTick - 19 + 1) = min(4, 3) = 3), not the original undeferred 4-tick plan.
        ai.StepMonster(monster, 19, StepCooldownTicks, tunables);
        Assert.True(monster.VerticalOffset > 0d, "the deferred leap never actually started (still grounded at tick 19).");

        for (uint tick = 20; tick < resolveTick; tick++)
        {
            ai.StepMonster(monster, tick, StepCooldownTicks, tunables);
            Assert.True(Distance(monster.Position, origin[0]) > 1e-3,
                $"landed EARLY at tick {tick} — the deferred duration was not shortened toward the deadline.");
        }

        ai.StepMonster(monster, resolveTick, StepCooldownTicks, tunables);

        // Lands exactly on the resolve tick despite the 1-tick-late start — never late, because the duration was
        // recomputed from the ticks remaining at the ACTUAL (deferred) start, not the original plan.
        Assert.True(Distance(monster.Position, origin[0]) <= 1e-3,
            $"deferred leap landed {monster.Position}, expected the locked origin {origin[0]} exactly on the resolve tick.");
        Assert.Equal(0d, monster.VerticalOffset, 1e-9);
        Assert.True(ai.TryGetPhase(monster.Id, out var after) && after == BasicRoamerBehavior.State.Chasing);
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

        Assert.True(ai.TryGetPhase(monster.Id, out var chasing) && chasing == BasicRoamerBehavior.State.Chasing);
        Assert.True(Distance(monster.Position, home) > 0.5d, "monster should have left home while chasing.");

        world.Remove(player.Id, out _);
        var resumed = false;
        for (uint tick = 13; tick <= 300; tick++)
        {
            ai.StepMonster(monster, tick, StepCooldownTicks, CombatTunables(pauseMin: 5, pauseMax: 5));
            Assert.True(ai.TryGetPhase(monster.Id, out var phase));
            Assert.NotEqual(BasicRoamerBehavior.State.Chasing, phase);
            if (Distance(monster.Position, home) <= HopLocomotion.ProgressEpsilonUnits && phase == BasicRoamerBehavior.State.Idle)
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
        Assert.True(ai.TryGetPhase(monster.Id, out var chasing) && chasing == BasicRoamerBehavior.State.Chasing);

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
                && phase == BasicRoamerBehavior.State.Idle)
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
        MonsterAiTunables T() => CombatTunables(
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
                && phase == BasicRoamerBehavior.State.Idle)
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
        Assert.True(ai.TryGetPhase(monster.Id, out var chasing) && chasing == BasicRoamerBehavior.State.Chasing);

        var bailed = false;
        for (uint tick = 2; tick <= 80; tick++)
        {
            ai.StepMonster(monster, tick, StepCooldownTicks, CombatTunables(pauseMin: 5, pauseMax: 5));
            AssertCollisionValid(monster.Position, grid.BlockedTiles, $"tick {tick}");
            if (ai.TryGetPhase(monster.Id, out var phase) && phase != BasicRoamerBehavior.State.Chasing)
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
                if (phase == BasicRoamerBehavior.State.Roaming)
                {
                    sawRoaming = true;
                }
                else if (phase == BasicRoamerBehavior.State.Idle && sawRoaming && tick > 50)
                {
                    sawIdleAfterRoaming = true;
                }
            }
        }

        Assert.True(sawRoaming, "monster never entered Roaming — the picker should hand it far targets to wedge on.");
        Assert.True(sawIdleAfterRoaming, "roaming monster appears frozen (never recovered to Idle after wedging).");
    }

    // ---------------------------------------------------------------------------------------------------------
    // MOVEMENT-ACTIONS (Phase C): the slime hop now goes THROUGH the shared executor as a real ballistic Jump.
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void Hop_RunsThroughExecutor_ArcsRealVerticalOffset_AndCadenceGatesMidArc()
    {
        // The Phase-C headline: a hop is no longer an instant teleport — the locomotion STARTS a ballistic Jump on the
        // shared executor (tick 0 = takeoff at origin, no XY move yet), and the executor advances the arc per tick: XY
        // moves one HopDistance along the locked heading while VerticalOffset arcs up to the apex and lands back to 0.
        // Mid-arc the IsActive gate makes a re-hop attempt return OnCooldown (no second action starts — no desync), and
        // even after the arc lands the TryBeginHop cadence still gates the next hop until the move window elapses.
        const uint cadence = 4;             // even, so a Z sample lands EXACTLY on the apex midpoint (i = N/2).
        var grid = OpenGrid();
        var world = new WorldState();
        var monster = SpawnMonster(world, new TileCoord(10, 10));
        var executor = CreateExecutor(grid, world);
        var locomotion = CreateLocomotion(grid, executor);

        var origin = monster.Position;
        var target = new WorldVector(origin.X + 6d, origin.Y); // due east, far enough that the hop clamps to full HopDistance.

        // Tick 1: START the hop. Reports Moved, the executor is now active, and tick 0 is takeoff (origin, grounded).
        Assert.Equal(HopResult.Moved, locomotion.Advance(monster, target, serverTick: 1, cadence));
        Assert.True(executor.IsActive(monster.Id));
        Assert.Equal(origin.X, monster.Position.X, 1e-9);
        Assert.Equal(0d, monster.VerticalOffset, 1e-9);

        // Drive the arc over `cadence` ticks; mid-arc a re-hop is gated to OnCooldown (the IsActive gate).
        var apex = 0d;
        for (uint t = 1; t <= cadence; t++)
        {
            executor.StepAll(world, t);
            apex = Math.Max(apex, monster.VerticalOffset);
            if (t < cadence)
            {
                Assert.Equal(HopResult.OnCooldown, locomotion.Advance(monster, target, t, cadence));
                Assert.True(executor.IsActive(monster.Id), "the in-flight arc must stay the ONLY active action mid-hop");
            }
        }

        // Landed: the action ended, XY advanced exactly one HopDistance east along the heading, Z snapped back to 0, and
        // the apex hit the real type height (the slime really rose — the cosmetic arc is gone).
        Assert.False(executor.IsActive(monster.Id));
        Assert.Equal(origin.X + HopDistance, monster.Position.X, 1e-6);
        Assert.Equal(origin.Y, monster.Position.Y, 1e-6);
        Assert.Equal(0d, monster.VerticalOffset, 1e-9);
        Assert.Equal(HopHeightUnits, apex, 1e-9);

        // The cadence still gates the NEXT hop: armed at tick 1 → next eligible at tick 1 + cadence. So a re-hop attempt
        // on the landing tick (cadence not yet elapsed) is OnCooldown; once the window elapses it Moves again.
        Assert.Equal(HopResult.OnCooldown, locomotion.Advance(monster, target, serverTick: cadence, cadence));
        Assert.Equal(HopResult.Moved, locomotion.Advance(monster, target, serverTick: 1 + cadence, cadence));
    }

    // ---------------------------------------------------------------------------------------------------------
    // MONSTER-BEHAVIOR P2 (docs/monster-behavior-design.md): the SAME roam brain driving a GlideLocomotion — a walker.
    // ---------------------------------------------------------------------------------------------------------

    // Builds a roam AI whose body is a GlideLocomotion (the continuous walk) instead of the hop, and seeds the
    // monster's walk speed (GameServer seeds SpeedUnitsPerSecond at spawn; here we set it on the bare entity).
    private static AiHarness CreateGlideAi(int seed, TileGrid grid, WorldState world)
        => CreateAi(seed, grid, world, locomotionFactory: (g, _) => CreateGlide(g, world));

    [Fact]
    public void Glider_RoamsToADestinationByGliding_ReachesItThenStops()
    {
        // A gnoll-like type (BasicRoamer brain + GlideLocomotion) roams to a destination by WALKING: it leaves Idle,
        // enters Roaming, physically moves away from home (continuous, sub-tile), reaches the destination and returns
        // to Idle — and on arrival its replicated Velocity is zeroed (the AI's Stop wiring), so the client parks it.
        const double roamRadius = 6d;
        var grid = OpenGrid();
        var world = new WorldState();
        var home = WorldVector.FromTile(new TileCoord(32, 32));
        var monster = SpawnMonster(world, home.ToTileRounded());
        monster.SetSpeedUnitsPerSecond(4.0d); // a non-zero walk speed (otherwise a glider can't move).
        var ai = CreateGlideAi(seed: 2024, grid, world);
        const uint pause = 3;
        ai.Register(monster, serverTick: 0, pauseMinTicks: pause, pauseMaxTicks: pause, aggroScanIntervalTicks: 10);

        var sawRoaming = false;
        var maxDistFromHome = 0d;
        var reachedIdleAfterRoaming = false;
        for (uint tick = 1; tick <= 600; tick++)
        {
            ai.StepMonster(monster, tick, StepCooldownTicks, RoamTunables(roamRadius, pause, pause));
            maxDistFromHome = Math.Max(maxDistFromHome, Distance(monster.Position, home));
            if (ai.TryGetPhase(monster.Id, out var phase))
            {
                if (phase == BasicRoamerBehavior.State.Roaming)
                {
                    sawRoaming = true;
                }
                else if (phase == BasicRoamerBehavior.State.Idle && sawRoaming && tick > pause + 5)
                {
                    // Returned to Idle after a real roam — it reached a destination. On every stop edge the AI Stops
                    // the locomotion, so a parked glider's replicated velocity is zero (the client stops extrapolating).
                    Assert.Equal(WorldVector.Zero, monster.Velocity);
                    reachedIdleAfterRoaming = true;
                }
            }
        }

        Assert.True(sawRoaming, "glider never entered Roaming.");
        Assert.True(maxDistFromHome > 0.5d, $"glider barely moved (max dist from home {maxDistFromHome:F3}) — it should WALK to a destination.");
        Assert.True(reachedIdleAfterRoaming, "glider never reached its destination + returned to Idle.");
    }

    [Fact]
    public void Glider_WedgedRoamer_TripsWatchdogToIdle_NeverPenetratesAWall()
    {
        // A walking monster boxed on all eight neighbours: whenever it picks a roam target outside the box its walk
        // slides to a fixpoint (Stuck every tick), the no-progress watchdog bails it back to Idle, and it re-picks.
        // It must never penetrate a wall and must keep cycling Roaming→(wedge)→Idle (not freeze permanently Roaming).
        var walls = EnclosingWalls(new TileCoord(10, 10));
        var grid = new TileGrid(GridSize, GridSize, walls);
        var world = new WorldState();
        var monster = SpawnMonster(world, new TileCoord(10, 10), networkId: 1);
        monster.SetSpeedUnitsPerSecond(4.0d);
        var ai = CreateGlideAi(seed: 31, grid, world);
        ai.Register(monster, serverTick: 0, pauseMinTicks: 2, pauseMaxTicks: 2, aggroScanIntervalTicks: 10);

        var sawRoaming = false;
        var sawIdleAfterRoaming = false;
        for (uint tick = 1; tick <= 2000; tick++)
        {
            ai.StepMonster(monster, tick, StepCooldownTicks, RoamTunables(4d, 2, 2));
            AssertCollisionValid(monster.Position, grid.BlockedTiles, $"tick {tick}");
            if (ai.TryGetPhase(monster.Id, out var phase))
            {
                if (phase == BasicRoamerBehavior.State.Roaming)
                {
                    sawRoaming = true;
                }
                else if (phase == BasicRoamerBehavior.State.Idle && sawRoaming && tick > 50)
                {
                    sawIdleAfterRoaming = true;
                }
            }
        }

        Assert.True(sawRoaming, "glider never entered Roaming — the picker should hand it far targets to wedge on.");
        Assert.True(sawIdleAfterRoaming, "wedged glider appears frozen (never recovered to Idle) — the watchdog failed.");
    }
}
