using Mmo.Server.Runtime;
using Mmo.Shared.Domain;
using Xunit;

namespace Mmo.Server.Tests;

public sealed class WorldEntityMovementTests
{
    [Fact]
    public void ValidStepMovesOneTileAndSetsFacing()
    {
        // Already facing the step direction, so it moves immediately.
        var entity = CreateEntity(tile: new TileCoord(8, 8), facing: Direction8.NE);
        var grid = new TileGrid(16, 16, []);

        var moved = entity.TryStep(Direction8.NE, 10, 4, grid, out var result);

        Assert.True(moved);
        Assert.Equal(new TileCoord(9, 7), entity.TileCoord);
        Assert.Equal(Direction8.NE, entity.Facing);
        Assert.Equal(2u, entity.StateRevision);
        Assert.True(result.Accepted);
        Assert.Equal("accepted", result.Reason);
        Assert.Equal(new TileCoord(8, 8), result.From);
        Assert.Equal(new TileCoord(9, 7), result.Target);
        Assert.Equal(entity.TileCoord, result.Result);
    }

    [Fact]
    public void StepInNewDirectionStepsImmediately()
    {
        // S98: turn-then-move removed. A step in a direction we don't face now STEPS immediately in that
        // direction (facing set on the step) — no separate turn beat, no extra tick.
        var entity = CreateEntity(tile: new TileCoord(8, 8), facing: Direction8.S);
        var grid = new TileGrid(16, 16, []);

        var moved = entity.TryStep(Direction8.NE, 10, 4, grid, out var move);

        Assert.True(moved);                                   // moved on the FIRST press in the new direction
        Assert.Equal(new TileCoord(9, 7), entity.TileCoord);       // advanced one tile in the new direction
        Assert.Equal(Direction8.NE, entity.Facing);           // facing set on the step
        Assert.Equal(2u, entity.StateRevision);               // a single bump for the accepted step (no turn bump)
        Assert.Equal(1u, entity.StepSequence);                // one accepted tile move
        Assert.True(move.Accepted);
        Assert.Equal("accepted", move.Reason);
    }

    [Fact]
    public void DirectionChangeStepsAtFullCooldown_NoTurnBeat()
    {
        // S98: with the turn delay gone, a direction change is just a step — the NEXT step in any direction is
        // gated only by the full step cooldown (no shorter turn-delay window). cooldown=10, step at tick 10
        // moves; tick 12 (< 20) is dropped on cooldown; tick 20 moves again.
        var entity = CreateEntity(tile: new TileCoord(8, 8), facing: Direction8.S);
        var grid = new TileGrid(16, 16, []);

        Assert.True(entity.TryStep(Direction8.E, 10, stepCooldownTicks: 10, grid, out var first));
        Assert.True(first.Accepted);
        Assert.Equal(new TileCoord(9, 8), entity.TileCoord);
        Assert.Equal(Direction8.E, entity.Facing);

        // Inside the full cooldown: dropped (no shorter turn-delay window any more).
        Assert.False(entity.TryStep(Direction8.E, 12, stepCooldownTicks: 10, grid, out var early));
        Assert.False(early.CooldownElapsed);
        Assert.Equal("cooldown", early.Reason);
        Assert.Equal(new TileCoord(9, 8), entity.TileCoord);

        // At tick 20 (full cooldown elapsed) the next step moves.
        Assert.True(entity.TryStep(Direction8.E, 20, stepCooldownTicks: 10, grid, out var move));
        Assert.True(move.Accepted);
        Assert.Equal(new TileCoord(10, 8), entity.TileCoord);
    }

    [Fact]
    public void BlockedDirectionChangeUpdatesAndReplicatesFacing()
    {
        // S98 replication detail: a direction change INTO A WALL (no tile move) must still update Facing AND
        // bump StateRevision so the new facing replicates (the Cato sprite flip depends on it).
        var entity = CreateEntity(tile: new TileCoord(8, 8), facing: Direction8.S);
        var grid = new TileGrid(16, 16, [new TileCoord(9, 8)]); // wall to the E

        var moved = entity.TryStep(Direction8.E, 10, 4, grid, out var result);

        Assert.False(moved);                                  // blocked — no tile move
        Assert.Equal(new TileCoord(8, 8), entity.TileCoord);       // held in place
        Assert.Equal(Direction8.E, entity.Facing);            // facing updated to the new direction
        Assert.Equal(2u, entity.StateRevision);               // bumped so the facing-only change replicates
        Assert.Equal(0u, entity.StepSequence);                // no accepted tile move
        Assert.False(result.TargetWalkable);
        Assert.Equal("blocked", result.Reason);
    }

    [Fact]
    public void RepeatedPressIntoSameWall_DoesNotBumpStateRevisionAgain()
    {
        // A repeated press into the SAME wall (no facing change) must not bump StateRevision — it would spam
        // snapshot deltas for nothing. Only the first press (which changed facing) bumped it.
        var entity = CreateEntity(tile: new TileCoord(8, 8), facing: Direction8.E);
        var grid = new TileGrid(16, 16, [new TileCoord(9, 8)]); // wall to the E, already facing E

        Assert.False(entity.TryStep(Direction8.E, 10, 4, grid)); // blocked, facing unchanged
        Assert.Equal(1u, entity.StateRevision);                  // no bump (facing already E)

        Assert.False(entity.TryStep(Direction8.E, 11, 4, grid)); // blocked again, facing unchanged
        Assert.Equal(1u, entity.StateRevision);                  // still no bump
    }

    [Fact]
    public void EarlyStepInsideCooldownIsDropped()
    {
        var entity = CreateEntity(tile: new TileCoord(8, 8), facing: Direction8.E);
        var grid = new TileGrid(16, 16, []);

        Assert.True(entity.TryStep(Direction8.E, 10, 4, grid)); // moves (already facing E)
        var moved = entity.TryStep(Direction8.E, 12, 4, grid, out var result);

        Assert.False(moved);
        Assert.Equal(new TileCoord(9, 8), entity.TileCoord);
        Assert.Equal(Direction8.E, entity.Facing);
        Assert.Equal(2u, entity.StateRevision);
        Assert.False(result.CooldownElapsed);
        Assert.Equal("cooldown", result.Reason);
    }

    [Fact]
    public void BlockedTileStepIsDropped()
    {
        // Already facing E so the step is a MOVE attempt — and the target is blocked.
        var entity = CreateEntity(tile: new TileCoord(8, 8), facing: Direction8.E);
        var grid = new TileGrid(16, 16, [new TileCoord(9, 8)]);

        var moved = entity.TryStep(Direction8.E, 10, 4, grid, out var result);

        Assert.False(moved);
        Assert.Equal(new TileCoord(8, 8), entity.TileCoord);
        Assert.Equal(Direction8.E, entity.Facing);
        Assert.Equal(1u, entity.StateRevision); // facing unchanged (already E) -> no bump
        Assert.False(result.TargetWalkable);
        Assert.Equal("blocked", result.Reason);
    }

    [Fact]
    public void OutOfBoundsStepIsDropped()
    {
        // Already facing N so the step is a MOVE attempt off the map.
        var entity = CreateEntity(tile: new TileCoord(0, 0), facing: Direction8.N);
        var grid = new TileGrid(16, 16, []);

        var moved = entity.TryStep(Direction8.N, 10, 4, grid, out var result);

        Assert.False(moved);
        Assert.Equal(new TileCoord(0, 0), entity.TileCoord);
        Assert.Equal(1u, entity.StateRevision); // facing unchanged (already N) -> no bump
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

        var moved = entity.TryStep(Direction8.NE, 10, 4, grid, out var result);

        Assert.False(moved);
        Assert.Equal(new TileCoord(8, 8), entity.TileCoord); // held — did not slip through the corner
        Assert.Equal(Direction8.NE, entity.Facing);
        Assert.Equal(1u, entity.StateRevision); // facing unchanged (already NE) -> no bump
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

        var moved = entity.TryStep(Direction8.NE, 10, 4, grid, out var result);

        Assert.False(moved);
        Assert.Equal(new TileCoord(8, 8), entity.TileCoord);
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

        var moved = entity.TryStep(Direction8.NE, 10, 4, grid, out var result);

        Assert.True(moved);
        Assert.Equal(new TileCoord(9, 7), entity.TileCoord);
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

        var moved = entity.TryStep(Direction8.E, 10, 4, blockedAdjacent, out var openResult);
        Assert.True(moved); // cardinal E to (9,8): destination open, no side-tile rule
        Assert.Equal(new TileCoord(9, 8), entity.TileCoord);
        Assert.True(openResult.Accepted);

        // Cardinal into a wall still holds (unchanged blocked behaviour).
        var wallEast = new TileGrid(16, 16, [new TileCoord(10, 8)]);
        var blocked = entity.TryStep(Direction8.E, 14, 4, wallEast, out var wallResult);
        Assert.False(blocked);
        Assert.Equal(new TileCoord(9, 8), entity.TileCoord); // held at the wall
        Assert.Equal("blocked", wallResult.Reason);
    }

    [Fact]
    public void EntitiesMayShareTile()
    {
        var first = CreateEntity(networkId: 1, tile: new TileCoord(8, 8), facing: Direction8.E);
        var second = CreateEntity(networkId: 2, tile: new TileCoord(8, 8), facing: Direction8.E);
        var grid = new TileGrid(16, 16, []);

        Assert.True(first.TryStep(Direction8.E, 10, 4, grid));
        Assert.True(second.TryStep(Direction8.E, 10, 4, grid));

        Assert.Equal(first.TileCoord, second.TileCoord);
    }

    [Fact]
    public void StepSequenceIncrementsOncePerAcceptedStep()
    {
        // S76: StepSequence starts at 0 and bumps by exactly 1 per ACCEPTED tile move.
        var entity = CreateEntity(tile: new TileCoord(8, 8), facing: Direction8.E);
        var grid = new TileGrid(16, 16, []);

        Assert.Equal(0u, entity.StepSequence);

        Assert.True(entity.TryStep(Direction8.E, 10, 4, grid)); // accepted
        Assert.Equal(1u, entity.StepSequence);

        Assert.True(entity.TryStep(Direction8.E, 14, 4, grid)); // accepted (cooldown elapsed)
        Assert.Equal(2u, entity.StepSequence);
    }

    [Fact]
    public void StepSequenceUnchangedOnBlockedDirectionChange()
    {
        // S98: a blocked direction change updates+replicates facing but does NOT move the tile — StepSequence
        // must NOT bump (it counts accepted tile moves only).
        var entity = CreateEntity(tile: new TileCoord(8, 8), facing: Direction8.S);
        var grid = new TileGrid(16, 16, [new TileCoord(9, 7)]); // wall NE of us

        Assert.False(entity.TryStep(Direction8.NE, 10, 4, grid, out var blocked)); // blocked direction change
        Assert.Equal("blocked", blocked.Reason);
        Assert.Equal(Direction8.NE, entity.Facing); // facing updated
        Assert.Equal(0u, entity.StepSequence);       // no tile move

        // A subsequent step into an open direction now moves and bumps the sequence.
        Assert.True(entity.TryStep(Direction8.E, 12, 4, grid)); // (8,8) -> (9,8) open
        Assert.Equal(1u, entity.StepSequence);
    }

    [Fact]
    public void StepSequenceUnchangedOnBlockedAndCooldownStep()
    {
        // S76: a blocked step (and an early/cooldown step) is rejected without moving — StepSequence must not
        // bump on either.
        var blockedGrid = new TileGrid(16, 16, [new TileCoord(9, 8)]);
        var blocked = CreateEntity(tile: new TileCoord(8, 8), facing: Direction8.E);
        Assert.False(blocked.TryStep(Direction8.E, 10, 4, blockedGrid)); // blocked
        Assert.Equal(0u, blocked.StepSequence);

        var openGrid = new TileGrid(16, 16, []);
        var cooldown = CreateEntity(tile: new TileCoord(8, 8), facing: Direction8.E);
        Assert.True(cooldown.TryStep(Direction8.E, 10, 4, openGrid)); // accepted -> seq 1
        Assert.Equal(1u, cooldown.StepSequence);
        Assert.False(cooldown.TryStep(Direction8.E, 12, 4, openGrid)); // early/cooldown drop
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
