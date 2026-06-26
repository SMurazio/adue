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
//
// CONTINUOUS MIGRATION (Phase 6): IsEntityInInterest now measures distance on the continuous float
// Position, while the grid still keys/gathers on the rounded TileCoord. The random layout therefore
// scatters entities at FRACTIONAL positions (not just tile centres) so the parity gate exercises the
// rounded-superset-vs-float-disc seam — the exact place the +1-tile gather margin (Phase 6) is
// load-bearing. If the gather were not a strict superset of the float disc, a sub-tile-further
// candidate would round into a cell just outside the gathered box and the grid set would drop it.
public sealed class SpatialAoiParityTests
{
    // Hysteresis margin GameServer adds to the interest radius for already-known entities, plus the Phase 6
    // rounded-gather superset margin. Kept in sync with GameServer.InterestExitHysteresisTiles (1 tile) +
    // GameServer.RoundedGatherMarginTiles (1 tile) so the query-radius computation matches the server's.
    private const float ExitHysteresisTiles = 1f;
    private const float RoundedGatherMarginTiles = GameServer.RoundedGatherMarginTiles;

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
                    var entity = state.AddTransient(
                        (uint)(i + 1),
                        EntityKind.Resource,
                        $"E{i}",
                        tile,
                        Direction8.S);
                    // Phase 6: nudge most entities OFF the tile centre to a fractional position so the
                    // float interest test and the rounded-tile gather diverge. ApplyResolvedMove writes the
                    // continuous Position; OnEntityMoved re-keys the grid bucket to the (possibly new)
                    // rounded tile — exactly what the live integrator does on a sub-tile advance.
                    if (random.Next(4) != 0)
                    {
                        var previous = entity.TileCoord;
                        var fx = tile.X + (random.NextDouble() - 0.5) * 1.9;
                        var fy = tile.Y + (random.NextDouble() - 0.5) * 1.9;
                        entity.ApplyResolvedMove(new WorldVector(fx, fy));
                        state.OnEntityMoved(entity, previous);
                    }

                    entities.Add(entity);
                }

                // Try several viewers (some are world entities, so always self-visible).
                for (var v = 0; v < 6; v++)
                {
                    WorldEntity viewer;
                    if (entities.Count > 0 && random.Next(2) == 0)
                    {
                        viewer = entities[random.Next(entities.Count)];
                    }
                    else
                    {
                        var viewerTile = new TileCoord(random.Next(-10, worldSize + 10), random.Next(-10, worldSize + 10));
                        viewer = MakeViewer(viewerTile);
                        // Phase 6: a synthetic viewer is also nudged off its tile centre so the gather (keyed
                        // on the viewer's ROUNDED tile) is centred up to 0.5 tile away from the true float
                        // position the interest test uses — the other half of the rounded-superset margin.
                        viewer.ApplyResolvedMove(new WorldVector(
                            viewerTile.X + (random.NextDouble() - 0.5) * 1.9,
                            viewerTile.Y + (random.NextDouble() - 0.5) * 1.9));
                    }

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

        var mover = state.AddTransient(1, EntityKind.Player, "Mover", new TileCoord(120, 100), Direction8.W);
        mover.SetSpeedUnitsPerSecond(10d); // 1 tile per integrate tick (10 units/s * 0.1s)

        var viewer = MakeViewer(new TileCoord(98, 100));

        // Initially out of a radius-5 interest box (dx = 22).
        Assert.DoesNotContain(1u, GridInInterest(state, viewer, session, interestRadius: 5f));

        // Walk the mover west via the continuous integrator until it is adjacent to the viewer, migrating its
        // grid bucket on every rounded-tile crossing (the bookkeeping Zone.IntegrateMovement does for a player).
        while (mover.TileCoord.X > 100)
        {
            var previous = mover.TileCoord;
            Assert.True(mover.IntegrateMovement(Direction8.W.ToUnitVector(), dtSeconds: 0.1d)); // crossed one tile west
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
        var viewer = MakeViewer(new TileCoord(100, 100));

        var mover = state.AddTransient(1, EntityKind.Player, "Mover", new TileCoord(100, 100), Direction8.E);
        mover.SetSpeedUnitsPerSecond(10d); // 1 tile per integrate tick

        var previous = mover.TileCoord;
        Assert.True(mover.IntegrateMovement(Direction8.E.ToUnitVector(), dtSeconds: 0.1d)); // -> (101,100), same cell
        state.OnEntityMoved(mover, previous);

        Assert.Contains(1u, GridInInterest(state, viewer, session, interestRadius: 10f));
    }

    // Phase 6: a candidate whose ROUNDED tile sits OUTSIDE the integer interest radius but whose true
    // continuous position is just INSIDE the float radius must now be selected — the inclusion the old
    // integer-tile distance could not express. Viewer at tile (100,100); radius 5. A candidate at tile
    // (105,100) is at integer distance exactly 5 (on the boundary), but nudged to x=104.6 its true float
    // distance is 4.6 < 5, so it is unambiguously inside. The mirror: nudged to x=105.4 (float distance
    // 5.4 > 5) it is unambiguously outside, even though its rounded tile (105) was on the integer boundary.
    [Fact]
    public void SubTilePositionJustInsideFloatRadiusIsSelected()
    {
        var state = new WorldState(gridCellSize: 8);
        var session = new ClientSession(null!);
        var viewer = MakeViewer(new TileCoord(100, 100));

        var inside = state.AddTransient(1, EntityKind.Resource, "Inside", new TileCoord(105, 100), Direction8.S);
        PlaceAt(state, inside, new WorldVector(104.6, 100));
        // The float distance is 4.6 < 5, so it is in interest even though it rounds to the boundary tile.
        Assert.True(GameServer.IsEntityInInterest(viewer, inside, session, interestRadius: 5f));
        Assert.Contains(1u, GridInInterest(state, viewer, session, interestRadius: 5f));
    }

    [Fact]
    public void SubTilePositionJustOutsideFloatRadiusIsExcluded()
    {
        var state = new WorldState(gridCellSize: 8);
        var session = new ClientSession(null!);
        var viewer = MakeViewer(new TileCoord(100, 100));

        var outside = state.AddTransient(1, EntityKind.Resource, "Outside", new TileCoord(105, 100), Direction8.S);
        PlaceAt(state, outside, new WorldVector(105.4, 100));
        // The float distance is 5.4 > 5 and it is unknown (not in last snapshot), so it is out of interest.
        Assert.False(GameServer.IsEntityInInterest(viewer, outside, session, interestRadius: 5f));
        // The gather still RETURNS it as a candidate (superset) — it must just fail the exact test.
        Assert.DoesNotContain(1u, GridInInterest(state, viewer, session, interestRadius: 5f));
    }

    // Phase 6 superset invariant, made deterministic at the rounding edge: a KNOWN candidate sitting on the
    // .5 boundary so its ROUNDED tile (the grid cell key) is the maximum half-tile FURTHER from the viewer
    // than its true float position, with its float distance just inside the exit (hysteresis) radius. The
    // gathered candidate set (keyed on rounded tiles) MUST still contain it — the property
    // RoundedGatherMarginTiles guarantees. (NB: with AwayFromZero rounding + the inclusive boundary, a
    // single-axis case can't quite force the bare ceil(exit) box to drop an in-interest entity — see the
    // review note; the +1 margin is the conservative provable bound and the randomized fractional sweep
    // above is the broad guard. This test pins the named edge as a regression scaffold.)
    [Fact]
    public void KnownCandidateAtRoundingEdgeStaysGathered()
    {
        const float interestRadius = 5f; // exit radius = 5 + 1 = 6
        var state = new WorldState(gridCellSize: 8);
        var session = new ClientSession(null!);

        var viewer = MakeViewer(new TileCoord(100, 100)); // exact tile centre, rounds to 100
        var candidate = state.AddTransient(1, EntityKind.Resource, "Edge", new TileCoord(106, 100), Direction8.S);
        PlaceAt(state, candidate, new WorldVector(105.5, 100)); // float dist 5.5 < 6; rounds to tile 106
        session.RememberSnapshotEntities([candidate]); // known ⇒ exit radius (hysteresis) applies

        // Float distance 5.5 ≤ exit radius 6 ⇒ in interest; the rounded-tile gather must still contain it.
        Assert.True(GameServer.IsEntityInInterest(viewer, candidate, session, interestRadius));
        AssertGatherIsSupersetOfFloatInterest(state, viewer, session, interestRadius, [candidate]);
        Assert.Contains(1u, GridInInterest(state, viewer, session, interestRadius));
    }

    // Directly asserts the Phase 6 superset invariant: for `viewer`, EVERY entity that passes the exact
    // float IsEntityInInterest test is present in the rounded-tile candidate gather. This is the property
    // RoundedGatherMarginTiles guarantees; a failure is a dropped in-interest entity (a replication hole).
    private static void AssertGatherIsSupersetOfFloatInterest(
        WorldState state,
        WorldEntity viewer,
        ClientSession session,
        float interestRadius,
        IReadOnlyList<WorldEntity> entities)
    {
        var radiusTiles = (int)Math.Ceiling(interestRadius + ExitHysteresisTiles + RoundedGatherMarginTiles);
        var gathered = new List<WorldEntity>();
        state.GatherInterestCandidates(viewer.TileCoord, radiusTiles, gathered);
        var gatheredIds = gathered.Select(static e => e.NetworkId).ToHashSet();

        foreach (var entity in entities)
        {
            if (GameServer.IsEntityInInterest(viewer, entity, session, interestRadius))
            {
                Assert.Contains(entity.NetworkId, gatheredIds);
            }
        }
    }

    // Places an entity at a continuous (possibly fractional) world position and re-keys its grid bucket,
    // exactly as the live integrator does on a sub-tile advance (ApplyResolvedMove + OnEntityMoved).
    private static void PlaceAt(WorldState state, WorldEntity entity, WorldVector position)
    {
        var previous = entity.TileCoord;
        entity.ApplyResolvedMove(position);
        state.OnEntityMoved(entity, previous);
    }

    // Replicates GameServer's exact two-source selection using the grid: gather candidates from the
    // spatial index over the exit-radius cell box, then filter by the exact interest test.
    private static SortedSet<uint> GridInInterest(
        WorldState state,
        WorldEntity viewer,
        ClientSession session,
        float interestRadius)
    {
        var radiusTiles = (int)Math.Ceiling(interestRadius + ExitHysteresisTiles + RoundedGatherMarginTiles);
        var candidates = new List<WorldEntity>();
        state.GatherInterestCandidates(viewer.TileCoord, radiusTiles, candidates);

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
