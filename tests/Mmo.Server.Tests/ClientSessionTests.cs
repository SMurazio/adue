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

        session.AcknowledgeSnapshot(3, serverTick: 10);
        session.AcknowledgeSnapshot(2, serverTick: 11);
        session.AcknowledgeSnapshot(4, serverTick: 12);

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
    public void AckAdvancesAckedBaselineForCarriedEntities()
    {
        var session = new ClientSession(null!);
        var entity = CreateEntity(networkId: 7, revision: 3);

        // Before any ack, the entity is unacked → it must be (re)sent.
        Assert.False(session.HasAckedCurrentRevision(entity));

        var seq = session.NextSnapshotSequence();
        var pending = session.BeginPendingSnapshot(seq, serverTick: 100);
        pending.Add(entity.NetworkId, entity.StateRevision);

        // Sent but not yet acked: still unacked.
        Assert.False(session.HasAckedCurrentRevision(entity));

        session.AcknowledgeSnapshot(seq, serverTick: 101);

        // Acked at the current revision → no longer re-sent.
        Assert.True(session.HasAckedCurrentRevision(entity));
        Assert.Equal(0, session.PendingSnapshotCount);
    }

    [Fact]
    public void DroppedSnapshotLeavesEntityUnackedUntilLaterAck()
    {
        var session = new ClientSession(null!);
        var entity = CreateEntity(networkId: 7, revision: 5);

        // Seq 1 carries the entity but is "dropped" (never acked).
        var seq1 = session.NextSnapshotSequence();
        session.BeginPendingSnapshot(seq1, serverTick: 10).Add(entity.NetworkId, entity.StateRevision);

        // Seq 2 re-carries it (self-heal) and IS acked. Acking seq2 also drains seq1 (<= acked).
        var seq2 = session.NextSnapshotSequence();
        session.BeginPendingSnapshot(seq2, serverTick: 11).Add(entity.NetworkId, entity.StateRevision);

        Assert.False(session.HasAckedCurrentRevision(entity));

        session.AcknowledgeSnapshot(seq2, serverTick: 12);

        Assert.True(session.HasAckedCurrentRevision(entity));
        Assert.Equal(0, session.PendingSnapshotCount);
    }

    [Fact]
    public void ForgetEntityBaselineDropsAckedAndPendingCarry()
    {
        var session = new ClientSession(null!);
        var entity = CreateEntity(networkId: 7, revision: 4);

        var seq = session.NextSnapshotSequence();
        session.BeginPendingSnapshot(seq, serverTick: 10).Add(entity.NetworkId, entity.StateRevision);
        session.AcknowledgeSnapshot(seq, serverTick: 11);
        Assert.True(session.HasAckedCurrentRevision(entity));

        // AOI exit forgets the baseline.
        session.ForgetEntityBaseline(entity.NetworkId);
        Assert.False(session.HasAckedCurrentRevision(entity));

        // A stale pending carry that acks AFTER the forget must not re-establish the baseline (re-entry
        // at the same revision would otherwise be wrongly suppressed → silent desync).
        var seq2 = session.NextSnapshotSequence();
        session.BeginPendingSnapshot(seq2, serverTick: 11).Add(entity.NetworkId, entity.StateRevision);
        session.ForgetEntityBaseline(entity.NetworkId);
        session.AcknowledgeSnapshot(seq2, serverTick: 12);

        Assert.False(session.HasAckedCurrentRevision(entity));
    }

    [Fact]
    public void ForceFullRebaselineClearsBaselineAndPending()
    {
        var session = new ClientSession(null!);
        var entity = CreateEntity(networkId: 7, revision: 2);

        var seq = session.NextSnapshotSequence();
        session.BeginPendingSnapshot(seq, serverTick: 10).Add(entity.NetworkId, entity.StateRevision);
        session.AcknowledgeSnapshot(seq, serverTick: 11);
        Assert.True(session.HasAckedCurrentRevision(entity));

        // Outstanding (unacked) snapshot plus an acked baseline.
        var seq2 = session.NextSnapshotSequence();
        session.BeginPendingSnapshot(seq2, serverTick: 11).Add(entity.NetworkId, entity.StateRevision);
        Assert.Equal(1, session.PendingSnapshotCount);

        session.ForceFullRebaseline();

        Assert.False(session.HasAckedCurrentRevision(entity));
        Assert.Equal(0, session.PendingSnapshotCount);
        Assert.Equal(0u, session.UnackedAgeTicks(serverTick: 50));
    }

    [Fact]
    public void UnackedAgeTracksOldestOutstandingSnapshot()
    {
        var session = new ClientSession(null!);
        var entity = CreateEntity(networkId: 7, revision: 1);

        Assert.Equal(0u, session.UnackedAgeTicks(serverTick: 100));

        var seq1 = session.NextSnapshotSequence();
        session.BeginPendingSnapshot(seq1, serverTick: 100).Add(entity.NetworkId, entity.StateRevision);
        var seq2 = session.NextSnapshotSequence();
        session.BeginPendingSnapshot(seq2, serverTick: 105).Add(entity.NetworkId, entity.StateRevision);

        // Age is measured from the OLDEST outstanding snapshot (seq1 @ tick 100).
        Assert.Equal(50u, session.UnackedAgeTicks(serverTick: 150));

        // Acking seq1 advances the oldest to seq2 @ tick 105.
        session.AcknowledgeSnapshot(seq1, serverTick: 150);
        Assert.Equal(45u, session.UnackedAgeTicks(serverTick: 150));

        session.AcknowledgeSnapshot(seq2, serverTick: 150);
        Assert.Equal(0u, session.UnackedAgeTicks(serverTick: 150));
    }

    [Fact]
    public void SilenceTicksSurvivesRebaselineAndResetsOnAck()
    {
        var session = new ClientSession(null!);
        var entity = CreateEntity(networkId: 7, revision: 1);

        // First snapshot starts the silence clock at tick 100.
        var seq1 = session.NextSnapshotSequence();
        session.BeginPendingSnapshot(seq1, serverTick: 100).Add(entity.NetworkId, entity.StateRevision);
        Assert.Equal(20u, session.SilenceTicks(serverTick: 120));

        // A forced re-baseline (cheap bound) does NOT reset the disconnect clock: still measured from 100.
        session.ForceFullRebaseline();
        Assert.Equal(0u, session.UnackedAgeTicks(serverTick: 140)); // re-baseline cleared the pending ring
        Assert.Equal(40u, session.SilenceTicks(serverTick: 140));   // but silence keeps growing

        // A later snapshot + ack resets the silence clock (client proved alive).
        var seq2 = session.NextSnapshotSequence();
        session.BeginPendingSnapshot(seq2, serverTick: 141).Add(entity.NetworkId, entity.StateRevision);
        session.AcknowledgeSnapshot(seq2, serverTick: 145);
        Assert.Equal(0u, session.SilenceTicks(serverTick: 145));
    }

    [Fact]
    public void KeepAliveIsDueBeforeFirstSnapshotAndOnceCadenceElapses()
    {
        var session = new ClientSession(null!);

        // Before any snapshot has been sent, a keep-alive is immediately due (so an idle viewer with no
        // delta from its first tick still gets something to ack).
        Assert.True(session.ShouldSendKeepAlive(serverTick: 5, cadenceTicks: 20));

        // Sending a snapshot resets the cadence clock; within the cadence window no keep-alive is due.
        session.RememberSnapshotSentTick(serverTick: 5);
        Assert.False(session.ShouldSendKeepAlive(serverTick: 10, cadenceTicks: 20));
        Assert.False(session.ShouldSendKeepAlive(serverTick: 24, cadenceTicks: 20));

        // Once the full cadence has elapsed, the next keep-alive is due again.
        Assert.True(session.ShouldSendKeepAlive(serverTick: 25, cadenceTicks: 20));
    }

    [Fact]
    public void KeepAliveSendSeedsTheDisconnectClockSoIdleWedgedViewersStillTimeOut()
    {
        var session = new ClientSession(null!);

        // A viewer whose delta is always empty only ever receives keep-alives (no BeginPendingSnapshot). The
        // keep-alive send must still seed the disconnect clock, otherwise SilenceTicks would stay 0 forever
        // and a genuinely wedged idle viewer would never be dropped.
        Assert.Equal(0u, session.SilenceTicks(serverTick: 200));

        session.RememberSnapshotSentTick(serverTick: 100);
        Assert.Equal(100u, session.SilenceTicks(serverTick: 200));
    }

    private static WorldEntity CreateEntity(uint networkId, uint revision)
    {
        var entity = new WorldEntity(
            id: networkId,
            networkId: networkId,
            EntityKind.Player,
            TileGrid.DefaultSpawnTile,
            Direction8.S,
            $"Player{networkId}",
            characterId: Guid.NewGuid(),
            ownerSession: null,
            isDurable: false);

        // StateRevision starts at 1 and is only bumped through movement/deplete; step the entity to reach
        // the requested revision so tests can assert against a known baseline value.
        var grid = new TileGrid(64, 64, []);
        var tick = 0u;
        while (entity.StateRevision < revision)
        {
            entity.TryStep(entity.Facing == Direction8.S ? Direction8.N : Direction8.S, tick, 0, 0, grid);
            tick++;
        }

        return entity;
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
