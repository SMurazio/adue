using Mmo.Server.Runtime;
using Mmo.Shared.Domain;
using Xunit;

namespace Mmo.Server.Tests;

public sealed class ClientSessionTests
{
    [Fact]
    public void ValidStepMovesOneTileAndSetsFacing()
    {
        var session = new ClientSession(null!);
        var grid = new TileGrid(16, 16, []);
        session.Authenticate(1, Guid.NewGuid(), "Player", ClientRole.Player, "sandbox", new TileCoord(8, 8));

        var moved = session.TryStep(Direction8.NE, 10, 4, grid);

        Assert.True(moved);
        Assert.Equal(new TileCoord(9, 7), session.Tile);
        Assert.Equal(Direction8.NE, session.Facing);
        Assert.Equal(10u, session.LastStepTick);
    }

    [Fact]
    public void EarlyStepInsideCooldownIsDropped()
    {
        var session = new ClientSession(null!);
        var grid = new TileGrid(16, 16, []);
        session.Authenticate(1, Guid.NewGuid(), "Player", ClientRole.Player, "sandbox", new TileCoord(8, 8));

        Assert.True(session.TryStep(Direction8.E, 10, 4, grid));
        var moved = session.TryStep(Direction8.E, 12, 4, grid);

        Assert.False(moved);
        Assert.Equal(new TileCoord(9, 8), session.Tile);
        Assert.Equal(Direction8.E, session.Facing);
        Assert.Equal(10u, session.LastStepTick);
    }

    [Fact]
    public void BlockedTileStepIsDropped()
    {
        var session = new ClientSession(null!);
        var grid = new TileGrid(16, 16, [new TileCoord(9, 8)]);
        session.Authenticate(1, Guid.NewGuid(), "Player", ClientRole.Player, "sandbox", new TileCoord(8, 8));

        var moved = session.TryStep(Direction8.E, 10, 4, grid);

        Assert.False(moved);
        Assert.Equal(new TileCoord(8, 8), session.Tile);
        Assert.Equal(Direction8.S, session.Facing);
    }

    [Fact]
    public void OutOfBoundsStepIsDropped()
    {
        var session = new ClientSession(null!);
        var grid = new TileGrid(16, 16, []);
        session.Authenticate(1, Guid.NewGuid(), "Player", ClientRole.Player, "sandbox", new TileCoord(0, 0));

        var moved = session.TryStep(Direction8.N, 10, 4, grid);

        Assert.False(moved);
        Assert.Equal(new TileCoord(0, 0), session.Tile);
    }

    [Fact]
    public void EntitiesMayShareTile()
    {
        var first = new ClientSession(null!);
        var second = new ClientSession(null!);
        var grid = new TileGrid(16, 16, []);
        first.Authenticate(1, Guid.NewGuid(), "First", ClientRole.Player, "sandbox", new TileCoord(8, 8));
        second.Authenticate(2, Guid.NewGuid(), "Second", ClientRole.Player, "sandbox", new TileCoord(8, 8));

        Assert.True(first.TryStep(Direction8.E, 10, 4, grid));
        Assert.True(second.TryStep(Direction8.E, 10, 4, grid));

        Assert.Equal(first.Tile, second.Tile);
    }

    [Fact]
    public void AcknowledgeSnapshotKeepsHighestSequence()
    {
        var session = new ClientSession(null!);

        session.AcknowledgeSnapshot(3);
        session.AcknowledgeSnapshot(2);
        session.AcknowledgeSnapshot(4);

        Assert.Equal(4u, session.LastAcknowledgedSnapshotSequence);
    }

    [Fact]
    public void NextSnapshotSequenceIncrementsPerSession()
    {
        var session = new ClientSession(null!);

        Assert.Equal(1u, session.NextSnapshotSequence());
        Assert.Equal(2u, session.NextSnapshotSequence());
    }
}
