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

    // LIVING-ENEMIES P3: the player-death respawn guard. MarkDead is idempotent (a flurry of hits can't reset the
    // timer or re-die), the respawn becomes due only after the delay, and MarkAlive clears it.
    [Fact]
    public void DeadStateSchedulesRespawnAndIsIdempotent()
    {
        var session = new ClientSession(null!);

        Assert.False(session.IsDead);
        Assert.True(session.MarkDead(serverTick: 100, respawnDelayTicks: 40));
        Assert.True(session.IsDead);
        Assert.Equal(140u, session.RespawnAtTick);

        // A second hit on the same downed player does NOT re-mark / reset the timer.
        Assert.False(session.MarkDead(serverTick: 105, respawnDelayTicks: 40));
        Assert.Equal(140u, session.RespawnAtTick);

        // Not due before the delay; due at/after.
        Assert.False(session.IsRespawnDue(139));
        Assert.True(session.IsRespawnDue(140));

        session.MarkAlive();
        Assert.False(session.IsDead);
        Assert.Null(session.RespawnAtTick);
        Assert.False(session.IsRespawnDue(99999));
    }

    // LIVING-ENEMIES P3: spawner-marker AOI bookkeeping mirrors the entity Knows/Remember/Forget trio.
    [Fact]
    public void SpawnerMarkerKnowledgeTracksAddAndForget()
    {
        var session = new ClientSession(null!);

        Assert.False(session.KnowsSpawner(5));
        session.RememberKnownSpawner(5);
        Assert.True(session.KnowsSpawner(5));
        Assert.Contains(5u, session.KnownSpawnerIds);

        Assert.True(session.ForgetKnownSpawner(5));
        Assert.False(session.KnowsSpawner(5));
        Assert.False(session.ForgetKnownSpawner(5)); // already gone.
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

        // StateRevision starts at 1 and is only bumped through movement/deplete; drive the entity through the
        // continuous integrator until its rounded tile crosses enough times to reach the requested revision
        // (ApplyResolvedMove bumps StateRevision once per rounded-tile crossing) so tests can assert against a
        // known baseline value. A 1-unit/tick eastward integrate crosses one tile per call.
        entity.SetSpeedUnitsPerSecond(10d);
        while (entity.StateRevision < revision)
        {
            entity.IntegrateMovement(Direction8.E.ToUnitVector(), dtSeconds: 0.1d); // +1 tile east per tick
        }

        return entity;
    }

    // CONTINUOUS MIGRATION (Phase 3, v36): a fresh per-input MoveIntent advances the integrate cursor + the keepalive
    // tick.
    [Fact]
    public void MoveInputAdvancesCursorAndTick()
    {
        var session = new ClientSession(null!);

        Assert.True(session.TryBeginMoveInput(1, serverTick: 10));

        Assert.Equal(1u, session.LastInputSeq);
        Assert.Equal(10u, session.LastMoveIntentTick);
    }

    // CONTINUOUS MIGRATION (Phase 3, v36): a stale/duplicate input seq (<= the cursor) is rejected and mutates nothing.
    [Fact]
    public void MoveInputRejectsStaleSequence()
    {
        var session = new ClientSession(null!);
        Assert.True(session.TryBeginMoveInput(5, serverTick: 10));

        Assert.False(session.TryBeginMoveInput(5, serverTick: 20));
        Assert.False(session.TryBeginMoveInput(4, serverTick: 20));

        Assert.Equal(5u, session.LastInputSeq);
        Assert.Equal(10u, session.LastMoveIntentTick);
    }

    // CONTINUOUS MIGRATION (Phase 3, v36): SetMoving tracks the "currently walking" flag; ClearMoveIntent stops it
    // WITHOUT touching the input cursor (a stale seq is still rejected after a force-stop).
    [Fact]
    public void ClearMoveIntentStopsButKeepsInputCursor()
    {
        var session = new ClientSession(null!);
        Assert.True(session.TryBeginMoveInput(3, serverTick: 5));
        session.SetMoving(true);
        Assert.True(session.IsMoving);

        session.ClearMoveIntent();

        Assert.False(session.IsMoving);
        Assert.False(session.TryBeginMoveInput(3, serverTick: 9));
        Assert.True(session.TryBeginMoveInput(4, serverTick: 9));
    }

    // CONTINUOUS MIGRATION (Phase 3, v36): the per-peer wall-clock dt BUDGET — the anti-speedhack core. A fresh peer
    // is seeded at the burst allowance, the budget accrues real elapsed time (capped at the allowance), and each
    // input may consume only the remaining budget so a FLOOD of max-dt inputs cannot out-integrate real time.
    [Fact]
    public void MoveDtBudgetClampsAFloodToRealElapsedTime()
    {
        const double burst = 0.4d;
        var session = new ClientSession(null!);

        // Seed (first credit) puts the budget at the burst allowance.
        session.CreditMoveDtBudget(0.05d, burst);

        // A flood of huge-dt inputs in a single tick can consume AT MOST the burst allowance, not more.
        var consumed = 0d;
        for (var i = 0; i < 100; i++)
        {
            consumed += session.ConsumeMoveDtBudget(1.0d); // each asks for a full second
        }

        Assert.True(consumed <= burst + 1e-9, $"flood consumed {consumed}s, exceeds the {burst}s burst allowance");
        // Budget drained: further inputs get nothing until real time credits it again.
        Assert.Equal(0d, session.ConsumeMoveDtBudget(1.0d));

        // Crediting a tick of real time re-credits ~that much (capped at the allowance), so steady play continues.
        session.CreditMoveDtBudget(0.05d, burst);
        Assert.True(session.ConsumeMoveDtBudget(1.0d) <= 0.05d + 1e-9);
    }

    // COMBAT-S2B: the attack cursor dedups stale/duplicate attack seqs, monotonically advancing on accept.
    [Fact]
    public void AttackCursorDedupsStaleSequences()
    {
        var session = new ClientSession(null!);

        Assert.True(session.TryConsumeAttackSequence(1));
        Assert.Equal(1u, session.LastAttackSeq);

        // A duplicate / re-ordered (<=) attack seq is rejected and does not advance.
        Assert.False(session.TryConsumeAttackSequence(1));
        Assert.False(session.TryConsumeAttackSequence(0));
        Assert.Equal(1u, session.LastAttackSeq);

        // A higher seq is accepted and advances the cursor.
        Assert.True(session.TryConsumeAttackSequence(5));
        Assert.Equal(5u, session.LastAttackSeq);
    }

    // COMBAT-S2B (the #1 rule): the attack stream's dedup cursor is FULLY INDEPENDENT of the move INPUT cursor.
    // Advancing the attack cursor must not touch LastInputSeq, and advancing that must not touch the attack cursor —
    // so an attack can never pre-dedup a movement input (or vice-versa), the NET6 desync bug. (v36: the move/commit
    // split is gone — there is one move INPUT cursor now.)
    [Fact]
    public void AttackCursorIsIndependentOfMoveInputCursor()
    {
        var session = new ClientSession(null!);

        // Advance the move input cursor to a high value.
        Assert.True(session.TryBeginMoveInput(50, serverTick: 1));
        Assert.Equal(50u, session.LastInputSeq);

        // A LOW attack seq (1) is still fresh on the attack cursor — the move cursor did not burn it.
        Assert.True(session.TryConsumeAttackSequence(1));
        Assert.Equal(1u, session.LastAttackSeq);

        // The attack did not move the input cursor.
        Assert.Equal(50u, session.LastInputSeq);

        // And a LOW move input seq is still rejected as stale (its own cursor is intact, untouched by the attack).
        Assert.False(session.TryBeginMoveInput(1, serverTick: 2));
        // While the move input cursor continues to advance independently, the attack cursor stays put.
        Assert.True(session.TryBeginMoveInput(51, serverTick: 2));
        Assert.Equal(1u, session.LastAttackSeq);
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
