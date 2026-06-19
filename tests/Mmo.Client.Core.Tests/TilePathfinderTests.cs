using System.Collections.Generic;
using System.Linq;
using Mmo.Shared.Domain;
using Xunit;

namespace Mmo.Client.Core.Tests;

public sealed class TilePathfinderTests
{
    // An open NxN grid with no blocked tiles.
    private static TilePathfinder OpenGrid(int width = 10, int height = 10)
    {
        return new TilePathfinder(width, height, new HashSet<TileCoord>());
    }

    [Fact]
    public void StraightLineOnOpenMap_TakesDiagonalThenStops()
    {
        var pathfinder = OpenGrid();

        var path = pathfinder.FindPath(new TileCoord(2, 2), new TileCoord(5, 5));

        // 8-way: a pure diagonal is 3 steps (one per tile), ending exactly on the goal.
        Assert.Equal(3, path.Count);
        Assert.Equal(new TileCoord(5, 5), path[^1]);
        AssertContiguous(new TileCoord(2, 2), path);
    }

    [Fact]
    public void OrthogonalLine_HasOneStepPerTile()
    {
        var pathfinder = OpenGrid();

        var path = pathfinder.FindPath(new TileCoord(1, 1), new TileCoord(1, 6));

        Assert.Equal(5, path.Count);
        Assert.Equal(new TileCoord(1, 6), path[^1]);
        AssertContiguous(new TileCoord(1, 1), path);
    }

    [Fact]
    public void RoutesAroundAWallSegment()
    {
        // A vertical wall at x=4 from y=0..3 (an open column gap at y=4 lets the path slip past below).
        var blocked = new HashSet<TileCoord>
        {
            new(4, 0), new(4, 1), new(4, 2), new(4, 3),
        };
        var pathfinder = new TilePathfinder(10, 10, blocked);

        var path = pathfinder.FindPath(new TileCoord(2, 1), new TileCoord(6, 1));

        Assert.NotEmpty(path);
        Assert.Equal(new TileCoord(6, 1), path[^1]);
        AssertContiguous(new TileCoord(2, 1), path);
        // The route never steps onto a blocked tile.
        Assert.All(path, tile => Assert.True(pathfinder.IsWalkable(tile)));
    }

    [Fact]
    public void UnreachableGoal_ReturnsEmpty()
    {
        // Fully wall off the goal cell: every neighbour of (5,5) is blocked.
        var blocked = new HashSet<TileCoord>();
        for (var dx = -1; dx <= 1; dx++)
        {
            for (var dy = -1; dy <= 1; dy++)
            {
                if (dx == 0 && dy == 0)
                {
                    continue;
                }

                blocked.Add(new TileCoord(5 + dx, 5 + dy));
            }
        }

        var pathfinder = new TilePathfinder(10, 10, blocked);

        var path = pathfinder.FindPath(new TileCoord(1, 1), new TileCoord(5, 5));

        Assert.Empty(path);
    }

    [Fact]
    public void GoalOnBlockedTile_ReturnsEmpty()
    {
        var blocked = new HashSet<TileCoord> { new(5, 5) };
        var pathfinder = new TilePathfinder(10, 10, blocked);

        var path = pathfinder.FindPath(new TileCoord(1, 1), new TileCoord(5, 5));

        Assert.Empty(path);
    }

    [Fact]
    public void StartEqualsGoal_ReturnsEmpty()
    {
        var pathfinder = OpenGrid();

        var path = pathfinder.FindPath(new TileCoord(3, 3), new TileCoord(3, 3));

        Assert.Empty(path);
    }

    [Fact]
    public void GoalOutOfBounds_ReturnsEmpty()
    {
        var pathfinder = OpenGrid(8, 8);

        Assert.Empty(pathfinder.FindPath(new TileCoord(2, 2), new TileCoord(8, 2)));
        Assert.Empty(pathfinder.FindPath(new TileCoord(2, 2), new TileCoord(-1, 2)));
    }

    [Fact]
    public void PathStaysInBounds()
    {
        var pathfinder = OpenGrid(6, 6);

        var path = pathfinder.FindPath(new TileCoord(1, 1), new TileCoord(4, 4));

        Assert.All(path, tile =>
        {
            Assert.InRange(tile.X, 0, 5);
            Assert.InRange(tile.Y, 0, 5);
        });
    }

    [Fact]
    public void MatchesServerWalkability_OverRegeneratedTerrain()
    {
        // Parity with the live map: build the pathfinder from the same generated zone the client holds and
        // confirm a sensible default-spawn-to-interior route exists and only steps walkable tiles.
        var zone = new ZoneModel("zone-1", 48, 48, 0, TerrainGenerator.CurrentGenVersion);
        var pathfinder = TilePathfinder.FromZone(zone);

        var start = new TileCoord(8, 8); // the carved-open legacy spawn tile
        Assert.True(pathfinder.IsWalkable(start));

        var path = pathfinder.FindPath(start, new TileCoord(30, 30));

        Assert.NotEmpty(path);
        Assert.All(path, tile => Assert.False(zone.IsBlocked(tile)));
        AssertContiguous(start, path);
    }

    // Asserts each step in the path is exactly one 8-way tile move from the previous (or from start).
    private static void AssertContiguous(TileCoord start, IReadOnlyList<TileCoord> path)
    {
        var previous = start;
        foreach (var tile in path)
        {
            var dx = System.Math.Abs(tile.X - previous.X);
            var dy = System.Math.Abs(tile.Y - previous.Y);
            Assert.True(dx <= 1 && dy <= 1 && (dx + dy) > 0, $"non-adjacent step {previous} -> {tile}");
            previous = tile;
        }
    }
}
