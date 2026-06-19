using Mmo.Client.Core;
using Xunit;

namespace Mmo.Client.Core.Tests;

public sealed class SnapshotContiguityTrackerTests
{
    [Fact]
    public void InOrderSequencesAdvanceContiguousToLatest()
    {
        var tracker = new SnapshotContiguityTracker();

        Assert.Equal(1u, tracker.Observe(1));
        Assert.Equal(2u, tracker.Observe(2));
        Assert.Equal(3u, tracker.Observe(3));
    }

    [Fact]
    public void GapStallsContiguousAtTopOfPrefix()
    {
        var tracker = new SnapshotContiguityTracker();

        // Received 1,2,3,5,6 with 4 missing → ack 3 (the top of the gap-free prefix). Sequences 5 and 6 are
        // remembered above the gap but cannot advance the cursor while 4 is absent.
        Assert.Equal(1u, tracker.Observe(1));
        Assert.Equal(2u, tracker.Observe(2));
        Assert.Equal(3u, tracker.Observe(3));
        Assert.Equal(3u, tracker.Observe(5));
        Assert.Equal(3u, tracker.Observe(6));
    }

    [Fact]
    public void FillingTheGapAdvancesPastAllAlreadyReceivedSequences()
    {
        var tracker = new SnapshotContiguityTracker();
        tracker.Observe(1);
        tracker.Observe(2);
        tracker.Observe(3);
        tracker.Observe(5);
        tracker.Observe(6);

        // Filling 4 unblocks the prefix and consumes the already-received 5,6 in one sweep → ack 6.
        Assert.Equal(6u, tracker.Observe(4));
    }

    [Fact]
    public void DuplicateOrStaleObservationDoesNotMoveCursor()
    {
        var tracker = new SnapshotContiguityTracker();
        tracker.Observe(1);
        tracker.Observe(2);

        Assert.Equal(2u, tracker.Observe(2)); // duplicate
        Assert.Equal(2u, tracker.Observe(1)); // stale
    }

    [Fact]
    public void CompleteSnapshotJumpsCursorOverAStalledGap()
    {
        var tracker = new SnapshotContiguityTracker();
        tracker.Observe(1);
        tracker.Observe(2);
        tracker.Observe(3);
        tracker.Observe(5); // gap at 4 stalls the cursor at 3

        Assert.Equal(3u, tracker.HighestContiguous);

        // A complete (re-baseline) snapshot at seq 50 re-establishes the full world: the cursor jumps to it
        // and the client acks 50 — this is how a permanently-lost middle sequence recovers after the server's
        // 2 s force-re-baseline, rather than stalling the ack at 3 forever.
        Assert.Equal(50u, tracker.Observe(50, isComplete: true));
    }

    [Fact]
    public void SequenceBeyondTheWindowJumpsTheCursor()
    {
        var tracker = new SnapshotContiguityTracker();
        tracker.Observe(1);
        tracker.Observe(2); // cursor at 2, gap at 3

        // Delivery runs > WindowSize (1024) ahead of the stalled cursor: the gap is unrecoverable here, so
        // the cursor jumps to the far-ahead sequence rather than stranding the ack at 2.
        Assert.Equal(2_000u, tracker.Observe(2_000));
    }

    [Fact]
    public void CursorLapsTheRingWithoutStaleBitFalsePositives()
    {
        var tracker = new SnapshotContiguityTracker();

        // Advance in order well past the ring size (1024) so slots are reused many times. Each in-order
        // observation must advance the cursor by exactly one: if the sweep failed to clear a consumed slot,
        // a later lap's matching slot would falsely advance the cursor too far.
        for (uint seq = 1; seq <= 5_000; seq++)
        {
            Assert.Equal(seq, tracker.Observe(seq));
        }

        // Now create a gap (skip 5_001) and deliver 5_002 in the next ring lap. The stale bit that slot 5_001
        // briefly held on the PREVIOUS lap (sequence 5_001 - 1024 = 3_977, long since consumed) must not let
        // the cursor advance past the genuine gap.
        Assert.Equal(5_000u, tracker.Observe(5_002));
        Assert.Equal(5_002u, tracker.Observe(5_001)); // filling the gap sweeps up to 5_002
    }
}
