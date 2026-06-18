using Mmo.Server.Runtime;
using Mmo.Shared.Domain;
using Xunit;

namespace Mmo.Server.Tests;

public sealed class WorldEntityMovementTests
{
    [Fact]
    public void ValidStepMovesOneTileAndSetsFacing()
    {
        var entity = CreateEntity(tile: new TileCoord(8, 8), facing: Direction8.S);
        var grid = new TileGrid(16, 16, []);

        var moved = entity.TryStep(Direction8.NE, 10, 4, grid, out var result);

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
    public void EarlyStepInsideCooldownIsDropped()
    {
        var entity = CreateEntity(tile: new TileCoord(8, 8), facing: Direction8.S);
        var grid = new TileGrid(16, 16, []);

        Assert.True(entity.TryStep(Direction8.E, 10, 4, grid));
        var moved = entity.TryStep(Direction8.E, 12, 4, grid, out var result);

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
        var entity = CreateEntity(tile: new TileCoord(8, 8), facing: Direction8.S);
        var grid = new TileGrid(16, 16, [new TileCoord(9, 8)]);

        var moved = entity.TryStep(Direction8.E, 10, 4, grid, out var result);

        Assert.False(moved);
        Assert.Equal(new TileCoord(8, 8), entity.Tile);
        Assert.Equal(Direction8.S, entity.Facing);
        Assert.Equal(1u, entity.StateRevision);
        Assert.False(result.TargetWalkable);
        Assert.Equal("blocked", result.Reason);
    }

    [Fact]
    public void OutOfBoundsStepIsDropped()
    {
        var entity = CreateEntity(tile: new TileCoord(0, 0), facing: Direction8.S);
        var grid = new TileGrid(16, 16, []);

        var moved = entity.TryStep(Direction8.N, 10, 4, grid, out var result);

        Assert.False(moved);
        Assert.Equal(new TileCoord(0, 0), entity.Tile);
        Assert.Equal(1u, entity.StateRevision);
        Assert.Equal("out_of_bounds", result.Reason);
    }

    [Fact]
    public void EntitiesMayShareTile()
    {
        var first = CreateEntity(networkId: 1, tile: new TileCoord(8, 8));
        var second = CreateEntity(networkId: 2, tile: new TileCoord(8, 8));
        var grid = new TileGrid(16, 16, []);

        Assert.True(first.TryStep(Direction8.E, 10, 4, grid));
        Assert.True(second.TryStep(Direction8.E, 10, 4, grid));

        Assert.Equal(first.Tile, second.Tile);
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
