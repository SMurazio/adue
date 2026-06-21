using Mmo.Server.Runtime;
using Mmo.Shared.Domain;
using Mmo.Shared.Protocol;
using Xunit;

namespace Mmo.Server.Tests;

// NET2: the redundant-unreliable StepCommitBatch packet feeds the EXISTING per-step commit path via
// GameServer.ExtractFreshStepCommits (pure: head + window deltas → fresh commits, ascending, deduped) and
// ClientSession.TryConsumeCommitSequence (the shared move-seq cursor). These tests pin the two behaviours the
// task calls out: (1) dedup — each commit seq applies at most once, in order, across redundant packets;
// (2) a "dropped head" batch's commit is recovered from a LATER batch's window. The cooldown-gated stepping
// (TryCommitStep) is untouched and not exercised here — only the delivery/dedup half (Stage 4 is authored-tick
// application). Mirrors MoveInputIngestTests for the commit channel.
public sealed class StepCommitBatchIngestTests
{
    // Mirrors HandleStepCommitBatch's cursor half: extract fresh commits (relative to the session's current
    // LastMoveSeq) and consume each through TryConsumeCommitSequence (the gate HandleStepCommit checks before
    // TryCommitStep). Returns the seqs that were actually consumed (fresh, in order).
    private static List<uint> Consume(ClientSession session, StepCommitBatchMessage batch, uint serverTick)
    {
        var consumed = new List<uint>();
        foreach (var (seq, _) in GameServer.ExtractFreshStepCommits(batch, session.LastMoveSeq))
        {
            if (session.TryConsumeCommitSequence(seq, serverTick))
            {
                consumed.Add(seq);
            }
        }

        return consumed;
    }

    [Fact]
    public void ExtractFreshStepCommits_OrdersWindowAscendingWithHeadLast()
    {
        // Head seq 10; window holds seqs 9, 8, 7 (deltas 1, 2, 3), in newest-first wire order.
        var batch = new StepCommitBatchMessage(
            HeadSeq: 10,
            Direction: Direction8.E,
            Window:
            [
                new StepCommitWindowEntry(1, Direction8.NE), // seq 9
                new StepCommitWindowEntry(2, Direction8.N),  // seq 8
                new StepCommitWindowEntry(3, Direction8.NW), // seq 7
            ]);

        var fresh = GameServer.ExtractFreshStepCommits(batch, lastSeq: 0);

        Assert.Equal(new uint[] { 7, 8, 9, 10 }, fresh.Select(static f => f.Seq).ToArray());
        // Oldest-first means the LAST applied is the head.
        Assert.Equal(Direction8.E, fresh[^1].Direction);
    }

    [Fact]
    public void ExtractFreshStepCommits_DropsAlreadySeenSeqs()
    {
        var batch = new StepCommitBatchMessage(
            HeadSeq: 5,
            Direction: Direction8.S,
            Window:
            [
                new StepCommitWindowEntry(1, Direction8.SE), // seq 4
                new StepCommitWindowEntry(2, Direction8.SW), // seq 3
            ]);

        // Already accepted up to seq 4: only seq 5 is fresh.
        var fresh = GameServer.ExtractFreshStepCommits(batch, lastSeq: 4);

        Assert.Equal(new uint[] { 5 }, fresh.Select(static f => f.Seq).ToArray());
    }

    [Fact]
    public void ExtractFreshStepCommits_DropsMalformedDeltas()
    {
        var batch = new StepCommitBatchMessage(
            HeadSeq: 3,
            Direction: Direction8.E,
            Window:
            [
                new StepCommitWindowEntry(0, Direction8.N),  // delta 0 aliases the head — dropped
                new StepCommitWindowEntry(5, Direction8.W),  // delta 5 > HeadSeq 3 underflows — dropped
                new StepCommitWindowEntry(1, Direction8.NE), // seq 2 — kept
            ]);

        var fresh = GameServer.ExtractFreshStepCommits(batch, lastSeq: 0);

        Assert.Equal(new uint[] { 2, 3 }, fresh.Select(static f => f.Seq).ToArray());
    }

    [Fact]
    public void DedupConsumesEachSeqOnceInOrder_AcrossRedundantBatches()
    {
        var session = new ClientSession(null!);

        // Batch A: head 2, window {1}. Both fresh → consumed 1, 2.
        var a = new StepCommitBatchMessage(2, Direction8.E,
        [
            new StepCommitWindowEntry(1, Direction8.N), // seq 1
        ]);
        Assert.Equal(new uint[] { 1, 2 }, Consume(session, a, serverTick: 10));
        Assert.Equal(2u, session.LastMoveSeq);

        // Batch B repeats 1 and 2 (redundant) and adds head 3. Only 3 is fresh — the repeats are deduped, so
        // the cooldown-gated commit fires at most once per step (no batched re-application → no speed-up).
        var b = new StepCommitBatchMessage(3, Direction8.S,
        [
            new StepCommitWindowEntry(1, Direction8.E), // seq 2 — already seen
            new StepCommitWindowEntry(2, Direction8.N), // seq 1 — already seen
        ]);
        Assert.Equal(new uint[] { 3 }, Consume(session, b, serverTick: 11));
        Assert.Equal(3u, session.LastMoveSeq);
    }

    [Fact]
    public void DroppedHeadBatch_RecoversFromLaterBatchWindow()
    {
        var session = new ClientSession(null!);

        // Establish commit seq 1.
        Consume(session, new StepCommitBatchMessage(1, Direction8.E, []), serverTick: 10);
        Assert.Equal(1u, session.LastMoveSeq);

        // The batch whose HEAD is seq 2 is DROPPED on the wire — never delivered. A LATER batch (head seq 3)
        // still carries seq 2 in its window. Both 2 and 3 are fresh; consuming oldest-first re-creates the
        // dropped commit then the head. Without the window, commit 2 would be lost (and a reliable retransmit
        // would instead arrive bunched with 3 — the cooldown gate would reject one → desync).
        var recovery = new StepCommitBatchMessage(3, Direction8.W,
        [
            new StepCommitWindowEntry(1, Direction8.S), // seq 2 — the dropped commit
        ]);
        var consumed = Consume(session, recovery, serverTick: 12);

        Assert.Equal(new uint[] { 2, 3 }, consumed); // the dropped head (2) recovered from the window
        Assert.Equal(3u, session.LastMoveSeq);
    }
}
