using Mmo.Server.Runtime;
using Mmo.Shared.Domain;
using Mmo.Shared.Protocol;
using Xunit;

namespace Mmo.Server.Tests;

// NET1 Stage 1: the redundant-unreliable MoveInput packet feeds the EXISTING held-intent path via
// GameServer.ExtractFreshMoveInputs (pure: head + window deltas → fresh inputs, ascending, deduped) and
// ClientSession.TryUpdateMoveIntent (seq cursor). These tests pin the two behaviours the task calls out:
// (1) dedup — each seq applies exactly once, in order; (2) a "dropped head" packet's state change is
// recovered from a LATER packet's window. The stepping model is untouched (not exercised here).
public sealed class MoveInputIngestTests
{
    // Applies a MoveInput packet exactly as the server does: extract fresh inputs (relative to the session's
    // current LastMoveSeq) and feed each through TryUpdateMoveIntent. Returns the seqs that were actually
    // applied (fresh, in order) so a test can assert what the session ingested.
    private static List<uint> Apply(ClientSession session, MoveInputMessage packet, uint serverTick)
    {
        var applied = new List<uint>();
        foreach (var (seq, moving, direction) in GameServer.ExtractFreshMoveInputs(packet, session.LastMoveSeq))
        {
            if (session.TryUpdateMoveIntent(seq, moving, direction, serverTick))
            {
                applied.Add(seq);
            }
        }

        return applied;
    }

    [Fact]
    public void ExtractFreshMoveInputs_OrdersWindowAscendingWithHeadLast()
    {
        // Head seq 10; window holds seqs 9, 8, 7 (deltas 1, 2, 3), in newest-first wire order.
        var packet = new MoveInputMessage(
            HeadSeq: 10,
            Moving: true,
            Direction: Direction8.E,
            Window:
            [
                new MoveInputWindowEntry(1, true, Direction8.NE), // seq 9
                new MoveInputWindowEntry(2, true, Direction8.N),  // seq 8
                new MoveInputWindowEntry(3, true, Direction8.NW), // seq 7
            ]);

        var fresh = GameServer.ExtractFreshMoveInputs(packet, lastSeq: 0);

        Assert.Equal(new uint[] { 7, 8, 9, 10 }, fresh.Select(static f => f.Seq).ToArray());
        // Oldest-first means the LAST applied is the head — the held intent ends on the head state.
        Assert.Equal(Direction8.E, fresh[^1].Direction);
    }

    [Fact]
    public void ExtractFreshMoveInputs_DropsAlreadySeenSeqs()
    {
        var packet = new MoveInputMessage(
            HeadSeq: 5,
            Moving: true,
            Direction: Direction8.S,
            Window:
            [
                new MoveInputWindowEntry(1, true, Direction8.SE), // seq 4
                new MoveInputWindowEntry(2, true, Direction8.SW), // seq 3
            ]);

        // Already accepted up to seq 4: only seq 5 is fresh.
        var fresh = GameServer.ExtractFreshMoveInputs(packet, lastSeq: 4);

        Assert.Equal(new uint[] { 5 }, fresh.Select(static f => f.Seq).ToArray());
    }

    [Fact]
    public void ExtractFreshMoveInputs_DropsMalformedDeltas()
    {
        var packet = new MoveInputMessage(
            HeadSeq: 3,
            Moving: true,
            Direction: Direction8.E,
            Window:
            [
                new MoveInputWindowEntry(0, true, Direction8.N),  // delta 0 aliases the head — dropped
                new MoveInputWindowEntry(5, true, Direction8.W),  // delta 5 > HeadSeq 3 underflows — dropped
                new MoveInputWindowEntry(1, true, Direction8.NE), // seq 2 — kept
            ]);

        var fresh = GameServer.ExtractFreshMoveInputs(packet, lastSeq: 0);

        Assert.Equal(new uint[] { 2, 3 }, fresh.Select(static f => f.Seq).ToArray());
    }

    [Fact]
    public void DedupAppliesEachSeqOnceInOrder_AcrossRedundantPackets()
    {
        var session = new ClientSession(null!);

        // Packet A: head 2, window {1}. Both fresh → applied 1, 2.
        var a = new MoveInputMessage(2, true, Direction8.E,
        [
            new MoveInputWindowEntry(1, true, Direction8.N), // seq 1
        ]);
        Assert.Equal(new uint[] { 1, 2 }, Apply(session, a, serverTick: 10));
        Assert.Equal(2u, session.LastMoveSeq);
        Assert.True(session.MoveIntentMoving);
        Assert.Equal(Direction8.E, session.MoveIntentDirection); // ended on the head

        // Packet B repeats 1 and 2 (redundant) and adds head 3. Only 3 is fresh — the repeats are deduped.
        var b = new MoveInputMessage(3, true, Direction8.S,
        [
            new MoveInputWindowEntry(1, true, Direction8.E), // seq 2 — already seen
            new MoveInputWindowEntry(2, true, Direction8.N), // seq 1 — already seen
        ]);
        Assert.Equal(new uint[] { 3 }, Apply(session, b, serverTick: 11));
        Assert.Equal(3u, session.LastMoveSeq);
        Assert.Equal(Direction8.S, session.MoveIntentDirection);
    }

    [Fact]
    public void DroppedHeadPacket_RecoversFromLaterPacketWindow()
    {
        var session = new ClientSession(null!);

        // Establish seq 1 (moving E).
        Apply(session, new MoveInputMessage(1, true, Direction8.E, []), serverTick: 10);
        Assert.Equal(1u, session.LastMoveSeq);

        // The packet whose HEAD is seq 2 (a STOP) is DROPPED on the wire — never delivered.
        // A LATER packet (head seq 3) still carries seq 2 in its window. Both 2 and 3 are fresh; applying
        // oldest-first re-creates the dropped STOP then the head. Without the window, the STOP would be lost.
        var recovery = new MoveInputMessage(3, true, Direction8.W,
        [
            new MoveInputWindowEntry(1, false, Direction8.N), // seq 2 — the dropped STOP (Moving=false)
        ]);
        var applied = Apply(session, recovery, serverTick: 12);

        Assert.Equal(new uint[] { 2, 3 }, applied); // the dropped head (2) recovered from the window
        Assert.Equal(3u, session.LastMoveSeq);
        // Held intent ends on the head (seq 3): moving W. The recovered STOP was applied then superseded.
        Assert.True(session.MoveIntentMoving);
        Assert.Equal(Direction8.W, session.MoveIntentDirection);
    }
}
