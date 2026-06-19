namespace Mmo.Client.Core;

// Tracks which snapshot sequences the client has fully received and exposes the highest *contiguously*
// received sequence — the top of the gap-free prefix (received 1,2,3,5,6 with 4 missing → 3). The client
// acks this value (S47a) instead of the latest sequence it saw, so the server (which advances each
// viewer's acked baseline for every snapshot seq <= ack) never advances past a sequence the client did
// NOT receive. A permanently-lost sequence stalls the cursor at the gap; the server's 2 s force-re-baseline
// then resyncs by sending a complete snapshot with a fresh sequence, which the tracker observes and the
// cursor jumps forward over the now-irrelevant pre-gap window.
//
// Bound: out-of-order sequences ABOVE the cursor are remembered in a fixed-size ring bitmap (WindowSize
// slots) keyed by sequence; memory is O(1) and there is no per-snapshot allocation. The window comfortably
// exceeds the in-flight snapshot count (the 2 s re-baseline bounds how far ahead delivery can run before a
// gap is force-healed), so a real reorder is captured. A sequence so far ahead of the cursor that it falls
// outside the window is treated as "received and contiguous from here": this only happens after a true
// stall (the gap will be re-baselined anyway), so the cursor jumps to it rather than stranding the client.
internal sealed class SnapshotContiguityTracker
{
    // Power-of-two so (sequence & Mask) maps a sequence to its ring slot. Snapshots are ~20/s and the
    // 2 s re-baseline bounds the in-flight window to ~40, so 1024 leaves a wide safety margin while staying
    // tiny (1 KB of bools).
    private const int WindowSize = 1024;
    private const uint Mask = WindowSize - 1;

    // _received[slot] is true iff the sequence that maps to slot AND lies in the live window
    // (cursor, cursor + WindowSize] has been received. Slots for sequences <= the cursor are stale and
    // ignored (the prefix is already gap-free). Cleared as the cursor sweeps past them.
    private readonly bool[] _received = new bool[WindowSize];

    // Top of the gap-free prefix. 0 means nothing received yet (sequences start at 1). Monotonic.
    private uint _highestContiguous;

    public uint HighestContiguous => _highestContiguous;

    // Records that snapshot `sequence` was fully received (reassembled + applied), then advances the
    // contiguous cursor as far as the received set allows. Returns the (possibly unchanged) cursor — the
    // value the client should ack. Sequences <= the current cursor are no-ops (already covered by the
    // gap-free prefix); a duplicate/stale delivery does not move the cursor.
    //
    // isComplete marks a FULL snapshot (server re-baseline / AOI entry): it re-establishes the whole visible
    // set independent of any prior delta, so the cursor JUMPS to it — discarding any stalled gap below it.
    // This is the convergence path for a permanently-lost middle sequence: the server's 2 s force-
    // re-baseline sends a complete snapshot, and observing it jumps the cursor so the ack advances and the
    // server stops re-baselining. Without this a lost middle sequence would stall the ack forever.
    public uint Observe(uint sequence, bool isComplete = false)
    {
        if (sequence <= _highestContiguous)
        {
            return _highestContiguous;
        }

        // A complete snapshot, or a sequence beyond the live window's reach (delivery ran far ahead of a
        // stalled cursor — the intervening gap is unrecoverable and will be re-baselined): jump the cursor
        // to this sequence so the client doesn't stall forever, and reset the window since every remembered
        // out-of-order slot now refers to a stale (pre-jump) sequence.
        if (isComplete || sequence - _highestContiguous > WindowSize)
        {
            Array.Clear(_received);
            _highestContiguous = sequence;
            return _highestContiguous;
        }

        _received[sequence & Mask] = true;

        // Walk the prefix forward over any already-received sequences, clearing each slot as we consume it
        // (so the window is reusable as the cursor laps the ring).
        var next = _highestContiguous + 1;
        while (_received[next & Mask])
        {
            _received[next & Mask] = false;
            _highestContiguous = next;
            next++;
        }

        return _highestContiguous;
    }
}
