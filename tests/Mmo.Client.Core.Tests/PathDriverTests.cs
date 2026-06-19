using System.Collections.Generic;
using Mmo.Shared.Domain;
using Xunit;

namespace Mmo.Client.Core.Tests;

public sealed class PathDriverTests
{
    [Fact]
    public void EmptyPath_StaysInactive()
    {
        var driver = new PathDriver();

        driver.Start(System.Array.Empty<TileCoord>());

        Assert.False(driver.IsActive);
        Assert.Equal(PathDriveAction.None, driver.Update(new TileCoord(0, 0)).Action);
    }

    [Fact]
    public void EmitsDirectionSequenceAsConfirmedTilesAdvance_ThenStopsAtDestination()
    {
        // Path: (1,1) -> E (2,1) -> SE (3,2) -> S (3,3). Start tile is excluded from the path list.
        var path = new List<TileCoord> { new(2, 1), new(3, 2), new(3, 3) };
        var driver = new PathDriver();
        driver.Start(path);

        // Confirmed still at start: steer toward the first waypoint (E).
        Assert.Equal(PathDriveCommand.Move(Direction8.E), driver.Update(new TileCoord(1, 1)));
        Assert.True(driver.IsActive);

        // Server confirms (2,1): steer toward (3,2) = SE.
        Assert.Equal(PathDriveCommand.Move(Direction8.SE), driver.Update(new TileCoord(2, 1)));

        // Server confirms (3,2): steer toward (3,3) = S.
        Assert.Equal(PathDriveCommand.Move(Direction8.S), driver.Update(new TileCoord(3, 2)));

        // Server confirms the destination: a single Stop, then driver is inactive.
        Assert.Equal(PathDriveCommand.Stop, driver.Update(new TileCoord(3, 3)));
        Assert.False(driver.IsActive);

        // Idempotent after arrival: no repeated Stop.
        Assert.Equal(PathDriveAction.None, driver.Update(new TileCoord(3, 3)).Action);
    }

    [Fact]
    public void HoldsDirection_WhileConfirmedTileHasNotAdvanced()
    {
        var path = new List<TileCoord> { new(2, 1), new(3, 1) };
        var driver = new PathDriver();
        driver.Start(path);

        // Multiple frames before the server confirms the first step: keep holding E.
        Assert.Equal(PathDriveCommand.Move(Direction8.E), driver.Update(new TileCoord(1, 1)));
        Assert.Equal(PathDriveCommand.Move(Direction8.E), driver.Update(new TileCoord(1, 1)));
        Assert.Equal(PathDriveCommand.Move(Direction8.E), driver.Update(new TileCoord(1, 1)));
    }

    [Fact]
    public void ToleratesConfirmedTileSkippingAWaypoint()
    {
        // Path of three orthogonal steps east; the confirmed tile jumps two waypoints at once.
        var path = new List<TileCoord> { new(2, 1), new(3, 1), new(4, 1) };
        var driver = new PathDriver();
        driver.Start(path);

        Assert.Equal(PathDriveCommand.Move(Direction8.E), driver.Update(new TileCoord(1, 1)));

        // Skip straight to (3,1): cursor advances past (2,1) and (3,1), steers toward (4,1).
        Assert.Equal(PathDriveCommand.Move(Direction8.E), driver.Update(new TileCoord(3, 1)));

        Assert.Equal(PathDriveCommand.Stop, driver.Update(new TileCoord(4, 1)));
        Assert.False(driver.IsActive);
    }

    [Fact]
    public void Cancel_StopsEmission_AndReportsWasActive()
    {
        var path = new List<TileCoord> { new(2, 1), new(3, 1) };
        var driver = new PathDriver();
        driver.Start(path);

        Assert.Equal(PathDriveCommand.Move(Direction8.E), driver.Update(new TileCoord(1, 1)));

        var wasActive = driver.Cancel();

        Assert.True(wasActive);
        Assert.False(driver.IsActive);
        Assert.Equal(PathDriveAction.None, driver.Update(new TileCoord(1, 1)).Action);
    }

    [Fact]
    public void Cancel_WhenIdle_ReportsNotActive()
    {
        var driver = new PathDriver();

        Assert.False(driver.Cancel());
    }

    [Fact]
    public void Desync_ConfirmedTileFarFromPath_StopsCleanly()
    {
        var path = new List<TileCoord> { new(2, 1), new(3, 1) };
        var driver = new PathDriver();
        driver.Start(path);

        Assert.Equal(PathDriveCommand.Move(Direction8.E), driver.Update(new TileCoord(1, 1)));

        // The avatar is confirmed somewhere far from the next waypoint (e.g. a teleport / big drift):
        // the driver stops cleanly rather than emitting a bogus multi-tile direction.
        Assert.Equal(PathDriveCommand.Stop, driver.Update(new TileCoord(20, 20)));
        Assert.False(driver.IsActive);
    }

    [Theory]
    [InlineData(0, -1, Direction8.N)]
    [InlineData(1, -1, Direction8.NE)]
    [InlineData(1, 0, Direction8.E)]
    [InlineData(1, 1, Direction8.SE)]
    [InlineData(0, 1, Direction8.S)]
    [InlineData(-1, 1, Direction8.SW)]
    [InlineData(-1, 0, Direction8.W)]
    [InlineData(-1, -1, Direction8.NW)]
    public void TryDirectionToward_MapsAdjacentDelta(int dx, int dy, Direction8 expected)
    {
        var from = new TileCoord(5, 5);
        var to = new TileCoord(5 + dx, 5 + dy);

        Assert.True(PathDriver.TryDirectionToward(from, to, out var direction));
        Assert.Equal(expected, direction);
        // Round-trip: the Direction8 delta must reproduce the move, proving parity with the server's step.
        Assert.Equal(to, from.Offset(direction.Delta().X, direction.Delta().Y));
    }

    [Fact]
    public void TryDirectionToward_RejectsNonAdjacent()
    {
        Assert.False(PathDriver.TryDirectionToward(new TileCoord(0, 0), new TileCoord(0, 0), out _));
        Assert.False(PathDriver.TryDirectionToward(new TileCoord(0, 0), new TileCoord(2, 0), out _));
    }
}
