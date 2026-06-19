using Mmo.Client.Core;
using Mmo.Shared.Domain;
using Xunit;

namespace Mmo.Client.Core.Tests;

// S53 click-to-move (UO greedy heading): each step picks the Direction8 toward the goal from the CURRENT
// (predicted) tile and holds it, stopping once on arrival. No A* waypoints — the heading IS the keyboard
// input. These tests drive the controller with a sequence of current tiles and assert the command sequence.
public sealed class ClickMoveControllerTests
{
    [Theory]
    [InlineData(0, -3, Direction8.N)]
    [InlineData(4, -4, Direction8.NE)]
    [InlineData(5, 0, Direction8.E)]
    [InlineData(3, 3, Direction8.SE)]
    [InlineData(0, 6, Direction8.S)]
    [InlineData(-2, 2, Direction8.SW)]
    [InlineData(-7, 0, Direction8.W)]
    [InlineData(-3, -3, Direction8.NW)]
    public void HeadingToward_PicksGreedy8DirToward_AnyDistanceGoal(int dx, int dy, Direction8 expected)
    {
        var from = new TileCoord(10, 10);
        var to = new TileCoord(10 + dx, 10 + dy);

        Assert.Equal(expected, ClickMoveController.HeadingToward(from, to));
    }

    [Fact]
    public void HeadingToward_UnequalAxes_ClosesTheLargerGapDiagonallyFirst()
    {
        // A goal 5 east, 2 north: the greedy heading is NE (move on both axes until one axis aligns), exactly
        // like holding the diagonal key toward the target.
        Assert.Equal(Direction8.NE, ClickMoveController.HeadingToward(new TileCoord(0, 0), new TileCoord(5, -2)));
    }

    [Fact]
    public void GoalEqualsStart_StaysInactive_NoCommand()
    {
        var controller = new ClickMoveController();

        controller.Start(new TileCoord(4, 4), new TileCoord(4, 4));

        Assert.False(controller.IsActive);
        Assert.Equal(PathDriveAction.None, controller.Update(new TileCoord(4, 4)).Action);
    }

    [Fact]
    public void DrivesGreedyHeading_ReAimingEachStep_ThenStopsOnArrival()
    {
        // Goal is 3 east + 1 south of the start. Greedy: SE until the south axis aligns, then E to the goal.
        var controller = new ClickMoveController();
        controller.Start(new TileCoord(0, 0), new TileCoord(3, 1));
        Assert.True(controller.IsActive);

        // From (0,0): dx=+3 dy=+1 -> SE.
        Assert.Equal(PathDriveCommand.Move(Direction8.SE), controller.Update(new TileCoord(0, 0)));
        // Predicted tile advances to (1,1): now dx=+2 dy=0 -> E (south axis aligned, re-aimed).
        Assert.Equal(PathDriveCommand.Move(Direction8.E), controller.Update(new TileCoord(1, 1)));
        Assert.Equal(PathDriveCommand.Move(Direction8.E), controller.Update(new TileCoord(2, 1)));

        // Arrived at the goal: a single Stop, then inactive and idempotent.
        Assert.Equal(PathDriveCommand.Stop, controller.Update(new TileCoord(3, 1)));
        Assert.False(controller.IsActive);
        Assert.Equal(PathDriveAction.None, controller.Update(new TileCoord(3, 1)).Action);
    }

    [Fact]
    public void HoldsHeading_WhileCurrentTileHasNotAdvanced()
    {
        var controller = new ClickMoveController();
        controller.Start(new TileCoord(0, 0), new TileCoord(3, 0));

        // Several frames before the predicted tile advances: keep emitting E.
        Assert.Equal(PathDriveCommand.Move(Direction8.E), controller.Update(new TileCoord(0, 0)));
        Assert.Equal(PathDriveCommand.Move(Direction8.E), controller.Update(new TileCoord(0, 0)));
        Assert.Equal(PathDriveCommand.Move(Direction8.E), controller.Update(new TileCoord(0, 0)));
    }

    [Fact]
    public void Cancel_StopsEmission_AndReportsWasActive()
    {
        var controller = new ClickMoveController();
        controller.Start(new TileCoord(0, 0), new TileCoord(3, 0));

        Assert.Equal(PathDriveCommand.Move(Direction8.E), controller.Update(new TileCoord(0, 0)));

        Assert.True(controller.Cancel());
        Assert.False(controller.IsActive);
        Assert.Equal(PathDriveAction.None, controller.Update(new TileCoord(0, 0)).Action);
    }

    [Fact]
    public void Cancel_WhenIdle_ReportsNotActive()
    {
        Assert.False(new ClickMoveController().Cancel());
    }
}
