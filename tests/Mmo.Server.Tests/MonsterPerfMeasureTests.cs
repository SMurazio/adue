using System.Diagnostics;
using Mmo.Server.Runtime;
using Mmo.Shared.Domain;
using Mmo.Shared.Domain.Actions;
using Xunit;
using Xunit.Abstractions;

namespace Mmo.Server.Tests;

// SLIME-LAG investigation harness. Measures the per-tick cost of a single monster's AI: hop cadence, StateRevision
// bumps (snapshot-inclusion driver), and wall-query count per tick. Not an assertion suite — it prints numbers so the
// hot path can be pinned with data. Mirrors the GameServer wiring exactly (real HopLocomotion + TileGrid).
public sealed class MonsterPerfMeasureTests
{
    private readonly ITestOutputHelper _out;
    public MonsterPerfMeasureTests(ITestOutputHelper output) => _out = output;

    private const int GridSize = 64;
    private const uint StepCooldownTicks = 3;   // base cadence — one hop per 3 ticks.
    private const double BodyRadius = 0.5d;
    private const double HopDistance = 1.0d;
    private const double HopHeightUnits = 0.5d; // the slime's real ballistic apex (Phase C).
    private const uint TickHz = 20;             // server tick rate (for the per-second math + the ballistic constants).

    // wall-query call counter, mutated by the wrapped delegate (now counts BOTH the locomotion's decision probe AND the
    // executor's per-tick arc resolve — Phase C splits the single old resolve into a probe + a per-tick arc).
    private int _wallQueryCalls;

    // The action executor that drives the hop arc (Phase C). Stored so RunAndReport can StepAll each tick like GameServer.
    private ServerActionExecutor _executor = null!;

    // MONSTER-BEHAVIOR P1: the locomotion is passed PER STEP now (GameServer resolves it per-type each tick), so the
    // one HopLocomotion is stored here and threaded into StepMonster from RunAndReport.
    private IMonsterLocomotion _locomotion = null!;

    private BasicRoamerBehavior CreateAi(
        int seed,
        TileGrid grid,
        WorldState world,
        BasicRoamerBehavior.FindTargetDelegate? findTarget = null,
        BasicRoamerBehavior.TryResolveTargetDelegate? tryResolve = null,
        BasicRoamerBehavior.AttackDelegate? attack = null)
    {
        // One wrapped wall-query shared by the executor + the locomotion so the counter sees the TOTAL wall work.
        void CountedQuery(WorldVector start, WorldVector delta, double radius, List<ContinuousCollision.Wall> scratch)
        {
            _wallQueryCalls++;
            grid.QueryNearbyWalls(start, delta, radius, scratch);
        }

        _executor = new ServerActionExecutor(
            (int)TickHz,
            () => BodyRadius,
            CountedQuery,
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

        _locomotion = new HopLocomotion(
            () => HopDistance,
            () => BodyRadius,
            CountedQuery,
            (monster, heading, hopDistance, cooldownTicks, serverTick) =>
            {
                var def = MovementActionRegistry.BuildForwardArcJump(
                    ActionId.Jump,
                    durationTicks: cooldownTicks,
                    jumpHeight: HopHeightUnits,
                    forwardDistanceUnits: hopDistance,
                    cooldownTicks: 0,
                    animationId: 1);
                return _executor.TryStart(monster, def, heading, serverTick);
            },
            id => _executor.IsActive(id));
        return new BasicRoamerBehavior(
            seed,
            grid.IsWalkable,
            findTarget ?? ((WorldEntity _, int _, out ulong id, out WorldVector pos) => { id = 0; pos = default; return false; }),
            tryResolve ?? ((ulong _, out WorldVector pos, out bool alive) => { pos = default; alive = false; return false; }),
            attack ?? ((WorldEntity _, ulong _, int _) => { }));
    }

    private static MonsterAiTunables RoamTunables(double roamRadius, uint pauseMin, uint pauseMax)
        => new(roamRadius, pauseMin, pauseMax, 0d, 0d, 0d, 1.5d, 0, 1, 10);

    [Fact]
    public void Measure_SingleRoamingMonster_OpenGrid()
    {
        var grid = new TileGrid(GridSize, GridSize, []);
        var world = new WorldState();
        var ai = CreateAi(1234, grid, world);
        var monster = world.AddTransient(1, EntityKind.Monster, "Monster", new TileCoord(32, 32), Direction8.S);
        // Match the ship default slime roam tunables (roam radius 4, pause ~ a couple seconds).
        var t = RoamTunables(4d, 20, 60);
        ai.Register(monster, 0, t.PauseMinTicks, t.PauseMaxTicks, t.AggroScanIntervalTicks);

        RunAndReport("ROAM open grid", ai, world, monster, t, ticks: 6000);
    }

    [Fact]
    public void Measure_SingleRoamingMonster_BoxedIn()
    {
        // Monster spawned with EVERY surrounding tile blocked — cannot make progress. Worst case for the roam-pick
        // fallback scan + the watchdog: does it re-pick + re-scan the leash box every tick?
        var centre = new TileCoord(32, 32);
        var walls = new List<TileCoord>();
        for (var dy = -1; dy <= 1; dy++)
        for (var dx = -1; dx <= 1; dx++)
            if (dx != 0 || dy != 0) walls.Add(new TileCoord(centre.X + dx, centre.Y + dy));

        var grid = new TileGrid(GridSize, GridSize, walls);
        var world = new WorldState();
        var ai = CreateAi(1234, grid, world);
        var monster = world.AddTransient(1, EntityKind.Monster, "Monster", centre, Direction8.S);
        var t = RoamTunables(4d, 20, 60);
        ai.Register(monster, 0, t.PauseMinTicks, t.PauseMaxTicks, t.AggroScanIntervalTicks);

        RunAndReport("ROAM boxed-in (every neighbor wall)", ai, world, monster, t, ticks: 6000);
    }

    [Fact]
    public void Measure_SingleChasingMonster()
    {
        var grid = new TileGrid(GridSize, GridSize, []);
        var world = new WorldState();
        // A stationary player just out of attack range so the monster chases forever (player health stays > 0).
        var player = world.AddTransient(2, EntityKind.Player, "Player", new TileCoord(40, 32), Direction8.S);
        player.SetMaxHealthFull(100);

        var ai = CreateAi(
            1234, grid, world,
            findTarget: (WorldEntity _, int _, out ulong id, out WorldVector pos) => { id = player.Id; pos = player.Position; return true; },
            tryResolve: (ulong _, out WorldVector pos, out bool alive) => { pos = player.Position; alive = player.Stats.Health > 0; return true; });
        var monster = world.AddTransient(1, EntityKind.Monster, "Monster", new TileCoord(32, 32), Direction8.S);
        // Aggro on, big leash so it keeps chasing the whole run.
        var t = new MonsterAiTunables(4d, 20, 60, 6d, 9d, 20d, 1.5d, 5, 20, 10);
        ai.Register(monster, 0, t.PauseMinTicks, t.PauseMaxTicks, t.AggroScanIntervalTicks);

        RunAndReport("CHASE stationary player", ai, world, monster, t, ticks: 6000);
    }

    private void RunAndReport(string label, BasicRoamerBehavior ai, WorldState world, WorldEntity monster, MonsterAiTunables t, int ticks)
    {
        _wallQueryCalls = 0;
        var positionChanges = 0;
        var revisionBumps = 0;
        var movedTrue = 0;
        var lastPos = monster.Position;
        var lastRev = monster.StateRevision;

        var sw = Stopwatch.StartNew();
        for (uint tick = 0; tick < ticks; tick++)
        {
            var moved = ai.StepMonster(monster, tick, StepCooldownTicks, t, _locomotion);
            // Phase C: advance the in-flight hop arc this tick, exactly like GameServer's StepMonsterAi → StepAll order.
            _executor.StepAll(world, tick);
            if (moved) movedTrue++;
            if (monster.Position != lastPos) { positionChanges++; lastPos = monster.Position; }
            if (monster.StateRevision != lastRev) { revisionBumps++; lastRev = monster.StateRevision; }
        }
        sw.Stop();

        var seconds = ticks / (double)TickHz;
        _out.WriteLine($"=== {label} === ({ticks} ticks = {seconds:F0}s sim @ {TickHz}Hz)");
        _out.WriteLine($"  wall-queries total:   {_wallQueryCalls,8}  ({_wallQueryCalls / seconds,7:F1}/s, {_wallQueryCalls / (double)ticks,5:F2}/tick)");
        _out.WriteLine($"  position changes:     {positionChanges,8}  ({positionChanges / seconds,7:F1}/s)");
        _out.WriteLine($"  StateRevision bumps:  {revisionBumps,8}  ({revisionBumps / seconds,7:F1}/s)");
        _out.WriteLine($"  StepMonster=>moved:   {movedTrue,8}  ({movedTrue / seconds,7:F1}/s)");
        _out.WriteLine($"  wall-clock for {ticks} ticks: {sw.Elapsed.TotalMilliseconds:F1} ms ({sw.Elapsed.TotalMilliseconds / ticks * 1000:F2} us/tick)");
    }
}
