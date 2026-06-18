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

    [Fact]
    public void FullSnapshotHeartbeatIsPhasedByNetworkId()
    {
        const uint heartbeatTicks = 20;
        var sessions = Enumerable.Range(1, 40)
            .Select(networkId =>
            {
                var session = new ClientSession(null!);
                session.Authenticate((uint)networkId, Guid.NewGuid(), $"Player{networkId}", ClientRole.Player, "sandbox", TileGrid.DefaultSpawnTile);
                return session;
            })
            .ToArray();

        var fullSnapshotCountsByTick = Enumerable.Range(1, (int)heartbeatTicks)
            .Select(tick => sessions.Count(session => session.ShouldSendFullSnapshot((uint)tick, heartbeatTicks)))
            .ToArray();

        Assert.All(fullSnapshotCountsByTick, count => Assert.Equal(2, count));
    }

    [Fact]
    public void FullSnapshotHeartbeatRepeatsOnSessionPhase()
    {
        const uint heartbeatTicks = 20;
        var session = new ClientSession(null!);
        session.Authenticate(7, Guid.NewGuid(), "Player", ClientRole.Player, "sandbox", TileGrid.DefaultSpawnTile);

        Assert.False(session.ShouldSendFullSnapshot(6, heartbeatTicks));
        Assert.True(session.ShouldSendFullSnapshot(7, heartbeatTicks));

        session.RememberSnapshotSent(7, isComplete: true);

        Assert.False(session.ShouldSendFullSnapshot(26, heartbeatTicks));
        Assert.True(session.ShouldSendFullSnapshot(27, heartbeatTicks));
    }

    [Fact]
    public void CollectSnapshotEntitiesMissingFromReusesDestination()
    {
        var session = new ClientSession(null!);
        session.RememberSnapshotEntities([1u, 2u, 3u]);
        var missing = new List<uint>();

        session.CollectSnapshotEntitiesMissingFrom(new HashSet<uint> { 1u, 3u }, missing);

        Assert.Equal([2u], missing);
    }
}
