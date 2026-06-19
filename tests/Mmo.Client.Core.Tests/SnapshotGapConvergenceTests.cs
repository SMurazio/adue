using Mmo.Client.Core;
using Mmo.Server.Runtime;
using Mmo.Shared.Domain;
using Xunit;

namespace Mmo.Client.Core.Tests;

// The case S46 could not cover: a MIDDLE snapshot is dropped while an entity steps. With the old
// "ack the latest received" model the server advanced the entity's acked baseline past the dropped
// sequence (because a LATER, acked snapshot's seq was >= the gap), silently skipping the entity's
// stepped revision — fatal for S47b's cumulative deltas. With S47a's highest-contiguous ack the gap
// stalls the ack, so the server never advances past it; convergence then comes from the gap filling
// or the server's 2 s force-re-baseline.
//
// The test pairs the real client-side SnapshotContiguityTracker (which computes the ack the client
// sends) with the real server-side ClientSession (the acked-baseline bookkeeping), modeling the UDP
// channel explicitly as deliver/drop. No sockets, fully deterministic.
public sealed class SnapshotGapConvergenceTests
{
    private const uint EntityNetworkId = 7;

    [Fact]
    public void DroppedMiddleSnapshotDoesNotAdvanceAckedBaselinePastTheGap()
    {
        var tracker = new SnapshotContiguityTracker();
        var session = new ClientSession(null!);

        // The entity sits at revision R1, then steps to R2 (the move carried by the to-be-dropped snapshot),
        // then steps again to R3 (carried by a later snapshot that DOES arrive and is acked).
        var entity = CreateEntity(EntityNetworkId);
        var r1 = entity.StateRevision;

        // Seq 1: carries the entity at R1. Delivered + acked → baseline at R1.
        SendAndAck(session, tracker, seq: 1, sentTick: 10, ackTick: 11, isComplete: true, (EntityNetworkId, r1));
        Assert.True(session.HasAckedCurrentRevision(entity));

        // Entity steps → R2. Seq 2 carries R2 but is DROPPED (never observed by the client → never acked).
        Step(entity);
        var r2 = entity.StateRevision;
        session.BeginPendingSnapshot(2, serverTick: 12).Add(EntityNetworkId, r2);
        // (client drops seq 2: no tracker.Observe, no ack)

        // Entity steps again → R3. Seq 3 carries R3, is delivered and the client acks — but the contiguous
        // cursor is still 1 (gap at 2), so the client acks 1, NOT 3.
        Step(entity);
        var r3 = entity.StateRevision;
        session.BeginPendingSnapshot(3, serverTick: 13).Add(EntityNetworkId, r3);
        var ack = tracker.Observe(3); // delivered, but stalls at the gap
        Assert.Equal(1u, ack);
        session.AcknowledgeSnapshot(ack, serverTick: 14);

        // The bug guard: the server must NOT have advanced the entity's baseline to R2 or R3. It stays at R1,
        // so the entity is re-sent (HasAckedCurrentRevision is false at the entity's CURRENT revision R3).
        Assert.False(session.HasAckedCurrentRevision(entity));
        Assert.NotEqual(r1, r3);

        // Seqs 2 and 3 are still pending on the server (oldest unacked is seq 2), so the safety clock is
        // ticking toward the 2 s re-baseline.
        Assert.Equal(2, session.PendingSnapshotCount);
    }

    [Fact]
    public void GapFilledByReorderConvergesToLatest()
    {
        var tracker = new SnapshotContiguityTracker();
        var session = new ClientSession(null!);
        var entity = CreateEntity(EntityNetworkId);

        SendAndAck(session, tracker, seq: 1, sentTick: 10, ackTick: 11, isComplete: true, (EntityNetworkId, entity.StateRevision));

        Step(entity);
        var r2 = entity.StateRevision;
        session.BeginPendingSnapshot(2, serverTick: 12).Add(EntityNetworkId, r2);

        Step(entity);
        var r3 = entity.StateRevision;
        session.BeginPendingSnapshot(3, serverTick: 13).Add(EntityNetworkId, r3);

        // Seq 3 arrives first (reorder): ack stalls at 1.
        Assert.Equal(1u, tracker.Observe(3));
        session.AcknowledgeSnapshot(1, serverTick: 14);
        Assert.False(session.HasAckedCurrentRevision(entity));

        // Seq 2 arrives late and fills the gap: the cursor sweeps 2 → 3, so the client acks 3.
        var ack = tracker.Observe(2);
        Assert.Equal(3u, ack);
        session.AcknowledgeSnapshot(ack, serverTick: 15);

        // The baseline advances to R3 (max revision across drained records), matching the entity's current
        // revision → converged, no re-send.
        Assert.True(session.HasAckedCurrentRevision(entity));
        Assert.Equal(0, session.PendingSnapshotCount);
    }

    [Fact]
    public void PermanentlyLostMiddleSnapshotConvergesViaForceRebaseline()
    {
        var tracker = new SnapshotContiguityTracker();
        var session = new ClientSession(null!);
        var entity = CreateEntity(EntityNetworkId);

        SendAndAck(session, tracker, seq: 1, sentTick: 10, ackTick: 11, isComplete: true, (EntityNetworkId, entity.StateRevision));

        // Seq 2 dropped forever; seq 3 delivered but stalled behind the gap.
        Step(entity);
        session.BeginPendingSnapshot(2, serverTick: 12).Add(EntityNetworkId, entity.StateRevision);
        Step(entity);
        var r3 = entity.StateRevision;
        session.BeginPendingSnapshot(3, serverTick: 13).Add(EntityNetworkId, r3);
        session.AcknowledgeSnapshot(tracker.Observe(3), serverTick: 14); // acks 1, stalled

        Assert.False(session.HasAckedCurrentRevision(entity));

        // The server's 2 s safety bound fires: ForceFullRebaseline clears the acked map + pending ring, then
        // re-sends a COMPLETE snapshot (every visible entity) at a fresh sequence. The client observes that
        // complete snapshot and the contiguous cursor JUMPS to it (gap discarded) → the ack advances.
        session.ForceFullRebaseline();
        var rebaselineSeq = 4u;
        var rRebaseline = entity.StateRevision; // current revision re-sent in the complete snapshot
        session.BeginPendingSnapshot(rebaselineSeq, serverTick: 1000).Add(EntityNetworkId, rRebaseline);

        var ack = tracker.Observe(rebaselineSeq, isComplete: true);
        Assert.Equal(rebaselineSeq, ack);
        session.AcknowledgeSnapshot(ack, serverTick: 1001);

        // Converged: the baseline matches the entity's current revision and nothing is left pending.
        Assert.True(session.HasAckedCurrentRevision(entity));
        Assert.Equal(0, session.PendingSnapshotCount);
    }

    [Fact]
    public void NoLossPathAcksLatestExactlyAsBefore()
    {
        var tracker = new SnapshotContiguityTracker();
        var session = new ClientSession(null!);
        var entity = CreateEntity(EntityNetworkId);

        // Every snapshot delivered in order: contiguous cursor == latest sequence, so the ack value matches
        // the old "ack the latest received" behavior and the baseline tracks the entity revision every step.
        for (uint seq = 1; seq <= 5; seq++)
        {
            if (seq > 1)
            {
                Step(entity);
            }

            var sentTick = 10 + seq;
            session.BeginPendingSnapshot(seq, sentTick).Add(EntityNetworkId, entity.StateRevision);
            var ack = tracker.Observe(seq, isComplete: seq == 1);
            Assert.Equal(seq, ack); // contiguous == latest, no loss
            session.AcknowledgeSnapshot(ack, sentTick + 1);
            Assert.True(session.HasAckedCurrentRevision(entity));
        }

        Assert.Equal(0, session.PendingSnapshotCount);
    }

    private static void SendAndAck(
        ClientSession session,
        SnapshotContiguityTracker tracker,
        uint seq,
        uint sentTick,
        uint ackTick,
        bool isComplete,
        params (uint NetworkId, uint Revision)[] carried)
    {
        var pending = session.BeginPendingSnapshot(seq, sentTick);
        foreach (var (networkId, revision) in carried)
        {
            pending.Add(networkId, revision);
        }

        var ack = tracker.Observe(seq, isComplete);
        session.AcknowledgeSnapshot(ack, ackTick);
    }

    private static void Step(WorldEntity entity)
    {
        var grid = new TileGrid(64, 64, []);
        var direction = entity.Facing == Direction8.S ? Direction8.N : Direction8.S;
        // Step until the revision actually advances (a blocked step would not bump it).
        var before = entity.StateRevision;
        var tick = entity.StateRevision; // any monotonic tick value works for the cooldown gate
        while (entity.StateRevision == before)
        {
            entity.TryStep(direction, tick, 0, 0, grid);
            direction = direction == Direction8.S ? Direction8.N : Direction8.S;
            tick++;
        }
    }

    private static WorldEntity CreateEntity(uint networkId)
    {
        return new WorldEntity(
            id: networkId,
            networkId: networkId,
            EntityKind.Player,
            TileGrid.DefaultSpawnTile,
            Direction8.S,
            $"Player{networkId}",
            characterId: Guid.NewGuid(),
            ownerSession: null,
            isDurable: false);
    }
}
