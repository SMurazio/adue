using Mmo.Server.Runtime;
using Mmo.Shared.Domain;
using Xunit;

namespace Mmo.Server.Tests;

// S41 parity gate: the grid-based AOI candidate gather (WorldState.GatherInterestCandidates) plus the
// exact per-entity interest test (GameServer.IsEntityInInterest) MUST return EXACTLY the same set of
// entities as a naive full scan over every world entity, for arbitrary layouts, radii, viewer
// positions, and hysteresis (last-snapshot) state. A drop at the cell-coverage edge would be both a
// visible bug and an anti-cheat hole, so this is the critical correctness test. Also covers the
// security invariant (outside-AOI ⇒ never selected) and index maintenance on spawn/move/despawn.
public sealed class SpatialAoiParityTests
{
    // Hysteresis margin GameServer adds to the interest radius for already-known entities. Kept in sync
    // with GameServer.InterestExitHysteresisTiles (1 tile) so the query-radius computation matches.
    private const float ExitHysteresisTiles = 1f;

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(7)]
    [InlineData(13)]
    [InlineData(42)]
    [InlineData(99)]
    public void GridGatherMatchesNaiveScanForRandomLayouts(int seed)
    {
        var random = new Random(seed);
        const int worldSize = 400;

        foreach (var cellSize in new[] { 1, 5, 16, 32, 41, 80 })
        {
            foreach (var interestRadius in new[] { 1f, 5f, 13.5f, 40f })
            {
                var state = new WorldState(cellSize);
                var entities = new List<WorldEntity>();
                var entityCount = random.Next(0, 250);
                for (var i = 0; i < entityCount; i++)
                {
                    var tile = new TileCoord(random.Next(0, worldSize), random.Next(0, worldSize));
                    entities.Add(state.AddTransient(
                        (uint)(i + 1),
                        EntityKind.Resource,
                        $"E{i}",
                        tile,
                        Direction8.S));
                }

                // Try several viewers (some are world entities, so always self-visible).
                for (var v = 0; v < 6; v++)
                {
                    var viewer = entities.Count > 0 && random.Next(2) == 0
                        ? entities[random.Next(entities.Count)]
                        : MakeViewer(new TileCoord(random.Next(-10, worldSize + 10), random.Next(-10, worldSize + 10)));

                    // Randomly mark a subset as "in last snapshot" to exercise the hysteresis path, which
                    // is exactly where an off-by-one in cell coverage would drop an edge entity.
                    var session = new ClientSession(null!);
                    var remembered = new List<WorldEntity>();
                    foreach (var entity in entities)
                    {
                        if (random.Next(3) == 0)
                        {
                            remembered.Add(entity);
                        }
                    }

                    session.RememberSnapshotEntities(remembered);

                    var naive = NaiveInInterest(viewer, entities, session, interestRadius);
                    var grid = GridInInterest(state, viewer, session, interestRadius);

                    Assert.Equal(naive, grid);

                    // Security invariant: nothing outside the exact interest test is ever in the grid set.
                    foreach (var networkId in grid)
                    {
                        var entity = entities.First(e => e.NetworkId == networkId);
                        Assert.True(GameServer.IsEntityInInterest(viewer, entity, session, interestRadius));
                    }
                }
            }
        }
    }

    [Fact]
    public void EntityFoundAfterSpawnAndNotAfterDespawn()
    {
        var state = new WorldState(gridCellSize: 16);
        var viewer = MakeViewer(new TileCoord(100, 100));
        var session = new ClientSession(null!);

        var entity = state.AddTransient(1, EntityKind.Resource, "Node", new TileCoord(102, 100), Direction8.S);
        Assert.Contains(1u, GridInInterest(state, viewer, session, interestRadius: 5f));

        Assert.True(state.Remove(entity.Id, out _));
        Assert.DoesNotContain(1u, GridInInterest(state, viewer, session, interestRadius: 5f));
    }

    [Fact]
    public void EntityTrackedAcrossCellBoundaryMove()
    {
        // Cell size 8: an entity stepping from x=103 to x=96 crosses the 96/104 cell boundary, exercising
        // the bucket-migration path. Both before and after, the grid result must match the naive scan.
        var state = new WorldState(gridCellSize: 8);
        var session = new ClientSession(null!);

        var mover = state.AddTransient(1, EntityKind.Player, "Mover", new TileCoord(120, 100), Direction8.S);
        var grid = new TileGrid(256, 256, blockedTiles: []);

        var viewer = MakeViewer(new TileCoord(98, 100));

        // Initially out of a radius-5 interest box (dx = 22).
        Assert.DoesNotContain(1u, GridInInterest(state, viewer, session, interestRadius: 5f));

        // Walk the mover west until it is adjacent to the viewer, stepping through the grid every tile.
        for (var tick = 1u; mover.Tile.X > 100; tick++)
        {
            var previous = mover.Tile;
            Assert.True(mover.TryStep(Direction8.W, tick, stepCooldownTicks: 1, grid));
            state.OnEntityMoved(mover, previous);

            var naive = NaiveInInterest(viewer, [mover], session, interestRadius: 5f);
            var fromGrid = GridInInterest(state, viewer, session, interestRadius: 5f);
            Assert.Equal(naive, fromGrid);
        }

        // Now within radius 5 (dx = 2).
        Assert.Contains(1u, GridInInterest(state, viewer, session, interestRadius: 5f));
    }

    [Fact]
    public void SameCellMoveKeepsEntityFindable()
    {
        var state = new WorldState(gridCellSize: 32);
        var session = new ClientSession(null!);
        var grid = new TileGrid(256, 256, blockedTiles: []);

        var mover = state.AddTransient(1, EntityKind.Player, "Mover", new TileCoord(100, 100), Direction8.S);
        var viewer = MakeViewer(new TileCoord(105, 100));

        var previous = mover.Tile;
        Assert.True(mover.TryStep(Direction8.E, serverTick: 1, stepCooldownTicks: 1, grid)); // -> (101,100), same cell
        state.OnEntityMoved(mover, previous);

        Assert.Contains(1u, GridInInterest(state, viewer, session, interestRadius: 10f));
    }

    // Replicates GameServer's exact two-source selection using the grid: gather candidates from the
    // spatial index over the exit-radius cell box, then filter by the exact interest test.
    private static SortedSet<uint> GridInInterest(
        WorldState state,
        WorldEntity viewer,
        ClientSession session,
        float interestRadius)
    {
        var radiusTiles = (int)Math.Ceiling(interestRadius + ExitHysteresisTiles);
        var candidates = new List<WorldEntity>();
        state.GatherInterestCandidates(viewer.Tile, radiusTiles, candidates);

        var result = new SortedSet<uint>();
        foreach (var candidate in candidates)
        {
            if (GameServer.IsEntityInInterest(viewer, candidate, session, interestRadius))
            {
                result.Add(candidate.NetworkId);
            }
        }

        return result;
    }

    private static SortedSet<uint> NaiveInInterest(
        WorldEntity viewer,
        IReadOnlyCollection<WorldEntity> entities,
        ClientSession session,
        float interestRadius)
    {
        var result = new SortedSet<uint>();
        foreach (var candidate in entities)
        {
            if (GameServer.IsEntityInInterest(viewer, candidate, session, interestRadius))
            {
                result.Add(candidate.NetworkId);
            }
        }

        return result;
    }

    private static WorldEntity MakeViewer(TileCoord tile)
    {
        return new WorldEntity(
            id: 0xFFFF,
            networkId: 0xFFFF,
            EntityKind.Player,
            tile,
            Direction8.S,
            "Viewer",
            Guid.NewGuid(),
            ownerSession: null,
            isDurable: true);
    }
}
