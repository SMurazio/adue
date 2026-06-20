using Mmo.Server.Runtime;
using Mmo.Shared.Domain;
using Xunit;

namespace Mmo.Server.Tests;

public sealed class WorldEntityMovementTests
{
    // Default turn delay (ticks) for the move/cooldown tests that aren't exercising the turn path. The
    // turn-specific tests pass their own value to make the turn-delay-vs-cooldown distinction explicit.
    private const uint TurnDelayTicks = 2;

    [Fact]
    public void ValidStepMovesOneTileAndSetsFacing()
    {
        // Already facing the step direction, so it moves immediately (no turn). Turn-then-move is covered
        // separately below.
        var entity = CreateEntity(tile: new TileCoord(8, 8), facing: Direction8.NE);
        var grid = new TileGrid(16, 16, []);

        var moved = entity.TryStep(Direction8.NE, 10, 4, TurnDelayTicks, grid, out var result);

        Assert.True(moved);
        Assert.Equal(new TileCoord(9, 7), entity.Tile);
        Assert.Equal(Direction8.NE, entity.Facing);
        Assert.Equal(2u, entity.StateRevision);
        Assert.True(result.Accepted);
        Assert.Equal("accepted", result.Reason);
        Assert.Equal(new TileCoord(8, 8), result.From);
        Assert.Equal(new TileCoord(9, 7), result.Target);
        Assert.Equal(entity.Tile, result.Result);
    }

    [Fact]
    public void StepInNewDirectionTurnsThenMoves()
    {
        // Turn-then-move (UO): a step in a direction we don't face turns in place (no move), and only the
        // next step in that direction moves.
        var entity = CreateEntity(tile: new TileCoord(8, 8), facing: Direction8.S);
        var grid = new TileGrid(16, 16, []);

        // First step in a new direction: TURN only. cooldown=4, turnDelay=2.
        var turned = entity.TryStep(Direction8.NE, 10, 4, TurnDelayTicks, grid, out var turn);
        Assert.False(turned);
        Assert.True(turn.Turned);
        Assert.Equal("turn", turn.Reason);
        Assert.Equal(new TileCoord(8, 8), entity.Tile); // did not move
        Assert.Equal(Direction8.NE, entity.Facing);     // turned to face it
        Assert.Equal(2u, entity.StateRevision);          // turn re-replicates facing

        // Next step in the same (now faced) direction: MOVE — eligible a TURN DELAY (2 ticks) after the turn,
        // i.e. at tick 12, NOT a full step cooldown (tick 14).
        var moved = entity.TryStep(Direction8.NE, 12, 4, TurnDelayTicks, grid, out var move);
        Assert.True(moved);
        Assert.False(move.Turned);
        Assert.Equal(new TileCoord(9, 7), entity.Tile);
        Assert.Equal(Direction8.NE, entity.Facing);
        Assert.Equal(3u, entity.StateRevision);
    }

    [Fact]
    public void TurnFreesNextStepAfterTurnDelay_NotFullStepCooldown()
    {
        // S63: a turn costs only the small turn delay. With cooldown=10 and turnDelay=3, a turn at tick 10
        // frees the next action at tick 13 — long before the full step cooldown would (tick 20).
        var entity = CreateEntity(tile: new TileCoord(8, 8), facing: Direction8.S);
        var grid = new TileGrid(16, 16, []);

        // Turn at tick 10 (S -> E). Sets next-eligible to 10 + turnDelay(3) = 13.
        Assert.False(entity.TryStep(Direction8.E, 10, stepCooldownTicks: 10, turnDelayTicks: 3, grid, out var turn));
        Assert.True(turn.Turned);
        Assert.Equal(Direction8.E, entity.Facing);

        // Before the turn delay elapses (tick 12 < 13): still on cooldown, no move.
        Assert.False(entity.TryStep(Direction8.E, 12, stepCooldownTicks: 10, turnDelayTicks: 3, grid, out var early));
        Assert.False(early.CooldownElapsed);
        Assert.Equal("cooldown", early.Reason);
        Assert.Equal(new TileCoord(8, 8), entity.Tile);

        // At tick 13 (turn delay elapsed, still well inside the full step cooldown that would end at 20) the
        // step in the now-faced direction MOVES.
        Assert.True(entity.TryStep(Direction8.E, 13, stepCooldownTicks: 10, turnDelayTicks: 3, grid, out var move));
        Assert.True(move.Accepted);
        Assert.Equal(new TileCoord(9, 8), entity.Tile);
    }

    [Fact]
    public void EarlyStepInsideCooldownIsDropped()
    {
        var entity = CreateEntity(tile: new TileCoord(8, 8), facing: Direction8.E);
        var grid = new TileGrid(16, 16, []);

        Assert.True(entity.TryStep(Direction8.E, 10, 4, TurnDelayTicks, grid)); // moves (already facing E)
        var moved = entity.TryStep(Direction8.E, 12, 4, TurnDelayTicks, grid, out var result);

        Assert.False(moved);
        Assert.Equal(new TileCoord(9, 8), entity.Tile);
        Assert.Equal(Direction8.E, entity.Facing);
        Assert.Equal(2u, entity.StateRevision);
        Assert.False(result.CooldownElapsed);
        Assert.Equal("cooldown", result.Reason);
    }

    [Fact]
    public void BlockedTileStepIsDropped()
    {
        // Already facing E so the step is a MOVE attempt (not a turn) — and the target is blocked.
        var entity = CreateEntity(tile: new TileCoord(8, 8), facing: Direction8.E);
        var grid = new TileGrid(16, 16, [new TileCoord(9, 8)]);

        var moved = entity.TryStep(Direction8.E, 10, 4, TurnDelayTicks, grid, out var result);

        Assert.False(moved);
        Assert.Equal(new TileCoord(8, 8), entity.Tile);
        Assert.Equal(Direction8.E, entity.Facing);
        Assert.Equal(1u, entity.StateRevision);
        Assert.False(result.TargetWalkable);
        Assert.Equal("blocked", result.Reason);
    }

    [Fact]
    public void OutOfBoundsStepIsDropped()
    {
        // Already facing N so the step is a MOVE attempt off the map (not a turn).
        var entity = CreateEntity(tile: new TileCoord(0, 0), facing: Direction8.N);
        var grid = new TileGrid(16, 16, []);

        var moved = entity.TryStep(Direction8.N, 10, 4, TurnDelayTicks, grid, out var result);

        Assert.False(moved);
        Assert.Equal(new TileCoord(0, 0), entity.Tile);
        Assert.Equal(1u, entity.StateRevision);
        Assert.Equal("out_of_bounds", result.Reason);
    }

    [Fact]
    public void DiagonalStepThroughCornerIsRejected_OneSideBlocked()
    {
        // S75: a diagonal NE step from (8,8) to (9,7) cuts between the side tiles (9,8) and (8,7). The
        // destination (9,7) is open, but one side (9,8) is blocked — the move would slip diagonally through the
        // wall corner, so it must be rejected and held (same shape as a blocked cardinal).
        var entity = CreateEntity(tile: new TileCoord(8, 8), facing: Direction8.NE);
        var grid = new TileGrid(16, 16, [new TileCoord(9, 8)]);

        var moved = entity.TryStep(Direction8.NE, 10, 4, TurnDelayTicks, grid, out var result);

        Assert.False(moved);
        Assert.Equal(new TileCoord(8, 8), entity.Tile); // held — did not slip through the corner
        Assert.Equal(Direction8.NE, entity.Facing);
        Assert.Equal(1u, entity.StateRevision);
        Assert.False(result.TargetWalkable);
        Assert.Equal("blocked", result.Reason); // in bounds, just blocked by the corner
    }

    [Fact]
    public void DiagonalStepThroughCornerIsRejected_BothSidesBlocked()
    {
        // S75: both side tiles (9,8) and (8,7) blocked — a fully-walled corner. The open destination (9,7) is
        // unreachable diagonally; the step is rejected and held.
        var entity = CreateEntity(tile: new TileCoord(8, 8), facing: Direction8.NE);
        var grid = new TileGrid(16, 16, [new TileCoord(9, 8), new TileCoord(8, 7)]);

        var moved = entity.TryStep(Direction8.NE, 10, 4, TurnDelayTicks, grid, out var result);

        Assert.False(moved);
        Assert.Equal(new TileCoord(8, 8), entity.Tile);
        Assert.Equal(1u, entity.StateRevision);
        Assert.False(result.TargetWalkable);
        Assert.Equal("blocked", result.Reason);
    }

    [Fact]
    public void DiagonalStepThroughOpenSpaceStillSucceeds()
    {
        // S75: both side tiles open ⇒ the diagonal is a clean move (corner check only rejects when a side is
        // blocked, never an open diagonal).
        var entity = CreateEntity(tile: new TileCoord(8, 8), facing: Direction8.NE);
        var grid = new TileGrid(16, 16, []);

        var moved = entity.TryStep(Direction8.NE, 10, 4, TurnDelayTicks, grid, out var result);

        Assert.True(moved);
        Assert.Equal(new TileCoord(9, 7), entity.Tile);
        Assert.Equal(Direction8.NE, entity.Facing);
        Assert.True(result.Accepted);
        Assert.Equal("accepted", result.Reason);
    }

    [Fact]
    public void CardinalStepIntoWallStillHolds_NoCornerRuleApplied()
    {
        // S75 guard: the corner rule must not touch cardinal steps. A blocked E target still holds with the same
        // "blocked" result, and an OPEN cardinal step past an adjacent-but-irrelevant blocked tile still moves
        // (a cardinal has no side tiles to check).
        var entity = CreateEntity(tile: new TileCoord(8, 8), facing: Direction8.E);
        var blockedAdjacent = new TileGrid(16, 16, [new TileCoord(8, 7)]); // (8,7) is N of us, irrelevant to E

        var moved = entity.TryStep(Direction8.E, 10, 4, TurnDelayTicks, blockedAdjacent, out var openResult);
        Assert.True(moved); // cardinal E to (9,8): destination open, no side-tile rule
        Assert.Equal(new TileCoord(9, 8), entity.Tile);
        Assert.True(openResult.Accepted);

        // Cardinal into a wall still holds (unchanged blocked behaviour).
        var wallEast = new TileGrid(16, 16, [new TileCoord(10, 8)]);
        var blocked = entity.TryStep(Direction8.E, 14, 4, TurnDelayTicks, wallEast, out var wallResult);
        Assert.False(blocked);
        Assert.Equal(new TileCoord(9, 8), entity.Tile); // held at the wall
        Assert.Equal("blocked", wallResult.Reason);
    }

    [Fact]
    public void EntitiesMayShareTile()
    {
        var first = CreateEntity(networkId: 1, tile: new TileCoord(8, 8), facing: Direction8.E);
        var second = CreateEntity(networkId: 2, tile: new TileCoord(8, 8), facing: Direction8.E);
        var grid = new TileGrid(16, 16, []);

        Assert.True(first.TryStep(Direction8.E, 10, 4, TurnDelayTicks, grid));
        Assert.True(second.TryStep(Direction8.E, 10, 4, TurnDelayTicks, grid));

        Assert.Equal(first.Tile, second.Tile);
    }

    [Fact]
    public void StepSequenceIncrementsOncePerAcceptedStep()
    {
        // S76: StepSequence starts at 0 and bumps by exactly 1 per ACCEPTED tile move.
        var entity = CreateEntity(tile: new TileCoord(8, 8), facing: Direction8.E);
        var grid = new TileGrid(16, 16, []);

        Assert.Equal(0u, entity.StepSequence);

        Assert.True(entity.TryStep(Direction8.E, 10, 4, TurnDelayTicks, grid)); // accepted
        Assert.Equal(1u, entity.StepSequence);

        Assert.True(entity.TryStep(Direction8.E, 14, 4, TurnDelayTicks, grid)); // accepted (cooldown elapsed)
        Assert.Equal(2u, entity.StepSequence);
    }

    [Fact]
    public void StepSequenceUnchangedOnTurnOnly()
    {
        // S76: a turn-then-move first action turns in place (no tile move) — StepSequence must NOT bump on the
        // turn, only on the subsequent accepted move.
        var entity = CreateEntity(tile: new TileCoord(8, 8), facing: Direction8.S);
        var grid = new TileGrid(16, 16, []);

        Assert.False(entity.TryStep(Direction8.NE, 10, 4, TurnDelayTicks, grid, out var turn)); // turn only
        Assert.True(turn.Turned);
        Assert.Equal(0u, entity.StepSequence); // turn did not move

        Assert.True(entity.TryStep(Direction8.NE, 12, 4, TurnDelayTicks, grid)); // now moves
        Assert.Equal(1u, entity.StepSequence);
    }

    [Fact]
    public void StepSequenceUnchangedOnBlockedAndCooldownStep()
    {
        // S76: a blocked step (and an early/cooldown step) is rejected without moving — StepSequence must not
        // bump on either.
        var blockedGrid = new TileGrid(16, 16, [new TileCoord(9, 8)]);
        var blocked = CreateEntity(tile: new TileCoord(8, 8), facing: Direction8.E);
        Assert.False(blocked.TryStep(Direction8.E, 10, 4, TurnDelayTicks, blockedGrid)); // blocked
        Assert.Equal(0u, blocked.StepSequence);

        var openGrid = new TileGrid(16, 16, []);
        var cooldown = CreateEntity(tile: new TileCoord(8, 8), facing: Direction8.E);
        Assert.True(cooldown.TryStep(Direction8.E, 10, 4, TurnDelayTicks, openGrid)); // accepted -> seq 1
        Assert.Equal(1u, cooldown.StepSequence);
        Assert.False(cooldown.TryStep(Direction8.E, 12, 4, TurnDelayTicks, openGrid)); // early/cooldown drop
        Assert.Equal(1u, cooldown.StepSequence); // unchanged
    }

    private static WorldEntity CreateEntity(
        uint networkId = 1,
        TileCoord? tile = null,
        Direction8 facing = Direction8.S)
    {
        return new WorldEntity(
            id: networkId,
            networkId: networkId,
            EntityKind.Player,
            tile ?? TileGrid.DefaultSpawnTile,
            facing,
            $"Player{networkId}",
            Guid.NewGuid(),
            ownerSession: null,
            isDurable: true);
    }
}
