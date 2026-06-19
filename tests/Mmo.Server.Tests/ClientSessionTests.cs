using Mmo.Server.Runtime;
using Mmo.Shared.Domain;
using Xunit;

namespace Mmo.Server.Tests;

public sealed class ClientSessionTests
{
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
                session.Authenticate((uint)networkId, Guid.NewGuid(), $"Player{networkId}", ClientRole.Player, "sandbox");
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
        session.Authenticate(7, Guid.NewGuid(), "Player", ClientRole.Player, "sandbox");

        Assert.False(session.ShouldSendFullSnapshot(6, heartbeatTicks));
        Assert.True(session.ShouldSendFullSnapshot(7, heartbeatTicks));

        session.RememberSnapshotSent(7, isComplete: true);

        Assert.False(session.ShouldSendFullSnapshot(26, heartbeatTicks));
        Assert.True(session.ShouldSendFullSnapshot(27, heartbeatTicks));
    }

    [Fact]
    public void MoveIntentUpdateRecordsStateAndTick()
    {
        var session = new ClientSession(null!);

        Assert.True(session.TryUpdateMoveIntent(1, moving: true, Direction8.E, serverTick: 10));

        Assert.True(session.MoveIntentMoving);
        Assert.Equal(Direction8.E, session.MoveIntentDirection);
        Assert.Equal(1u, session.LastMoveSeq);
        Assert.Equal(10u, session.LastMoveIntentTick);
    }

    [Fact]
    public void MoveIntentRejectsStaleSequence()
    {
        var session = new ClientSession(null!);
        session.TryUpdateMoveIntent(5, moving: true, Direction8.E, serverTick: 10);

        // Equal-or-lower sequences are stale: state and the intent tick must not change.
        Assert.False(session.TryUpdateMoveIntent(5, moving: false, Direction8.W, serverTick: 20));
        Assert.False(session.TryUpdateMoveIntent(4, moving: false, Direction8.W, serverTick: 20));

        Assert.True(session.MoveIntentMoving);
        Assert.Equal(Direction8.E, session.MoveIntentDirection);
        Assert.Equal(10u, session.LastMoveIntentTick);
    }

    [Fact]
    public void MoveIntentKeepaliveRefreshesTickWithSameDirection()
    {
        var session = new ClientSession(null!);
        session.TryUpdateMoveIntent(1, moving: true, Direction8.E, serverTick: 10);

        Assert.True(session.TryUpdateMoveIntent(2, moving: true, Direction8.E, serverTick: 30));

        Assert.True(session.MoveIntentMoving);
        Assert.Equal(30u, session.LastMoveIntentTick);
    }

    [Fact]
    public void ClearMoveIntentStopsButKeepsSequenceCursor()
    {
        var session = new ClientSession(null!);
        session.TryUpdateMoveIntent(3, moving: true, Direction8.N, serverTick: 5);

        session.ClearMoveIntent();

        Assert.False(session.MoveIntentMoving);
        // The sequence cursor is unchanged, so a stale seq is still rejected after a force-stop.
        Assert.False(session.TryUpdateMoveIntent(3, moving: true, Direction8.N, serverTick: 9));
        Assert.True(session.TryUpdateMoveIntent(4, moving: true, Direction8.N, serverTick: 9));
        Assert.True(session.MoveIntentMoving);
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
