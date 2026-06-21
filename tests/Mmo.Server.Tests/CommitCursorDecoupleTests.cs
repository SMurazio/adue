using Mmo.Server.Runtime;
using Mmo.Shared.Domain;
using Mmo.Shared.Protocol;
using Xunit;

namespace Mmo.Server.Tests;

// NET6 — the StepCommit dedup cursor is DECOUPLED from the MoveInput-intent cursor.
//
// ROOT CAUSE (verified): the client mints both StepCommit seqs and MoveInput-intent seqs off ONE shared monotonic
// counter, so a keyup STOP intent (seq N+1) carries a HIGHER seq than an unconfirmed tail commit (seq N). When the
// SERVER shared ONE dedup cursor for both streams (the old _lastMoveSeq), that stop intent burned the cursor past
// commit N, so every re-send of commit N was deduped away ("already seen") in ExtractFreshStepCommits BEFORE it
// could reach the (healthy) authored-tick commit gate. The tail (and any mid-stream commit stranded behind an
// interleaved direction-change intent) was permanently un-acceptable → the lead never drained → ForceResync snap.
//
// These tests model BOTH streams at the SERVER dedup boundary (exactly what HandleMoveInput / HandleStepCommitBatch
// do: ExtractFreshStepCommits/ExtractFreshMoveInputs gate on a cursor; TryUpdateMoveIntent/TryConsumeCommitSequence
// advance it). The MANDATE-1 reproduction uses a SharedCursorModel whose single-cursor logic is a verbatim copy of
// the PRE-FIX ClientSession methods — so it pins the exact bug NET5's harness could not see. The fix tests drive the
// REAL, now-decoupled ClientSession and assert the strand is gone.
public sealed class CommitCursorDecoupleTests
{
    // ---- A verbatim model of the OLD (pre-NET6) shared-cursor ClientSession dedup -------------------------------
    // ONE cursor advanced by BOTH intents and commits, both rejecting seq <= cursor. This is the code that shipped
    // and stranded commits live; it is kept here as the executable reproduction of the diagnosis. The fix tests
    // below run the REAL ClientSession to show the strand no longer happens.
    private sealed class SharedCursorModel
    {
        private uint _cursor;

        public uint Cursor => _cursor;

        // OLD TryUpdateMoveIntent: intent advances the shared cursor.
        public bool TryUpdateMoveIntent(uint sequence)
        {
            if (sequence <= _cursor)
            {
                return false;
            }

            _cursor = sequence;
            return true;
        }

        // OLD TryConsumeCommitSequence: commit advances the SAME shared cursor.
        public bool TryConsumeCommitSequence(uint sequence)
        {
            if (sequence <= _cursor)
            {
                return false;
            }

            _cursor = sequence;
            return true;
        }

        // Mirrors the OLD HandleStepCommitBatch: extract relative to the shared cursor, consume each.
        public List<uint> IngestCommitBatch(StepCommitBatchMessage batch)
        {
            var consumed = new List<uint>();
            foreach (var (seq, _, _) in GameServer.ExtractFreshStepCommits(batch, _cursor))
            {
                if (TryConsumeCommitSequence(seq))
                {
                    consumed.Add(seq);
                }
            }

            return consumed;
        }

        // Mirrors HandleMoveInput: extract relative to the shared cursor, apply each intent.
        public List<uint> IngestMoveInput(MoveInputMessage packet)
        {
            var applied = new List<uint>();
            foreach (var (seq, _, _) in GameServer.ExtractFreshMoveInputs(packet, _cursor))
            {
                if (TryUpdateMoveIntent(seq))
                {
                    applied.Add(seq);
                }
            }

            return applied;
        }
    }

    private static StepCommitBatchMessage Commit(uint seq, uint tick, Direction8 dir) =>
        new(seq, tick, dir, []);

    // A keyup STOP intent at `seq`, repeated as a redundant window down to `seq - redundancy + 1` (the client
    // ships the stop ~8x). All carry Moving=false.
    private static MoveInputMessage StopIntent(uint seq, int redundancy)
    {
        var window = new List<MoveInputWindowEntry>();
        for (var d = 1; d < redundancy; d++)
        {
            if (seq - (uint)d == 0)
            {
                break;
            }

            window.Add(new MoveInputWindowEntry((byte)d, false, Direction8.E));
        }

        return new MoveInputMessage(seq, false, Direction8.E, window);
    }

    // Mirrors the REAL (fixed) HandleStepCommitBatch: gate on LastCommitSeq, consume via TryConsumeCommitSequence.
    private static List<uint> RealIngestCommitBatch(ClientSession session, StepCommitBatchMessage batch, uint serverTick)
    {
        var consumed = new List<uint>();
        foreach (var (seq, _, _) in GameServer.ExtractFreshStepCommits(batch, session.LastCommitSeq))
        {
            if (session.TryConsumeCommitSequence(seq, serverTick))
            {
                consumed.Add(seq);
            }
        }

        return consumed;
    }

    // Mirrors the REAL HandleMoveInput: gate on LastMoveSeq, apply via TryUpdateMoveIntent.
    private static List<uint> RealIngestMoveInput(ClientSession session, MoveInputMessage packet, uint serverTick)
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

    // ---- MANDATE 1: reproduce the tail-on-stop strand on the OLD shared-cursor code -----------------------------
    //
    // Walk east: commits seq 1..N land. The TAIL commit N's batch is DROPPED on the wire. The keyup then mints a
    // STOP intent at seq N+1 (8x redundant), which lands and burns the shared cursor to N+1. Now the client re-sends
    // tail commit N (NET5's tail re-send) — but on the shared cursor it is seq N <= N+1, so ExtractFreshStepCommits
    // DEDUPES it. The commit never reaches the (healthy) commit gate. lead is stuck: server saw only up to N-1.
    [Fact]
    public void SharedCursor_StopIntentBurnsTailCommit_ResendIsDeduped_LeadStuck()
    {
        var model = new SharedCursorModel();
        const uint tail = 4; // commit seqs 1..4; #4 is the tail

        // Commits 1..3 land normally (their batches arrived). #4's batch is DROPPED — never delivered.
        Assert.Equal(new uint[] { 1 }, model.IngestCommitBatch(Commit(1, 30, Direction8.E)));
        Assert.Equal(new uint[] { 2 }, model.IngestCommitBatch(Commit(2, 33, Direction8.E)));
        Assert.Equal(new uint[] { 3 }, model.IngestCommitBatch(Commit(3, 36, Direction8.E)));
        Assert.Equal(3u, model.Cursor);

        // Keyup: STOP intent at seq tail+1 = 5 (8x redundant). It lands and BURNS the shared cursor to 5.
        var stopApplied = model.IngestMoveInput(StopIntent(tail + 1, redundancy: 8));
        Assert.Contains(tail + 1, stopApplied);
        Assert.Equal(tail + 1, model.Cursor);

        // NET5 tail re-send: the client re-ships commit #4 (seq 4) repeatedly. On the SHARED cursor 4 <= 5, so it
        // is DEDUPED every time — the bug. The server never consumes commit 4: it strands forever.
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var consumed = model.IngestCommitBatch(Commit(tail, 39, Direction8.E));
            Assert.Empty(consumed); // re-send deduped away → permanent strand
        }

        // Lead is stuck: the highest COMMIT actually consumed is 3, one behind the predicted tail 4.
        Assert.Equal(tail + 1, model.Cursor); // cursor sits on the STOP intent, NOT the tail commit
    }

    // ---- MANDATE 1b: reproduce the mid-stream strand (interleaved direction-change intent) on OLD code ----------
    //
    // While moving: commit 1 lands, commit 2's batch DROPS, then a direction-change INTENT at seq 3 lands and burns
    // the shared cursor to 3. The later commit's redundancy window re-carries commit 2 — but on the shared cursor
    // 2 <= 3, so it is DEDUPED. Commit 2 strands → the runaway.
    [Fact]
    public void SharedCursor_InterleavedIntentMidStream_StrandsDroppedCommit()
    {
        var model = new SharedCursorModel();

        // Commit 1 lands. Commit 2's batch is DROPPED (never delivered).
        Assert.Equal(new uint[] { 1 }, model.IngestCommitBatch(Commit(1, 30, Direction8.E)));

        // A direction-change INTENT takes seq 3 and lands, burning the shared cursor past the dropped commit 2.
        var applied = model.IngestMoveInput(new MoveInputMessage(3, true, Direction8.N, []));
        Assert.Equal(new uint[] { 3 }, applied);
        Assert.Equal(3u, model.Cursor);

        // A LATER commit (seq 4) re-carries the dropped commit 2 in its redundancy window (delta 2). On the shared
        // cursor, seq 2 <= 3 → DEDUPED; only seq 4 is fresh. Commit 2 is stranded → lead climbs (runaway).
        var recovery = new StepCommitBatchMessage(4, 42, Direction8.E,
        [
            new StepCommitWindowEntry(2, 9, Direction8.E), // seq 2 @ tick 33 — the dropped commit, lost to dedup
        ]);
        var consumed = model.IngestCommitBatch(recovery);
        Assert.DoesNotContain(2u, consumed); // commit 2 stranded on the shared cursor (only head seq 4 was fresh)
        Assert.Equal(new uint[] { 4 }, consumed);
    }

    // ---- MANDATE 2: the SAME tail-on-stop scenario DRAINS on the REAL decoupled ClientSession -------------------
    //
    // Identical sequence on the real, fixed ClientSession: the STOP intent advances LastMoveSeq only; the tail
    // commit re-send is gated on LastCommitSeq (=3), so seq 4 > 3 → FRESH → it lands. The commit cursor reaches 4;
    // the intent stays stopped. No strand.
    [Fact]
    public void DecoupledCursor_StopIntentDoesNotBurnTailCommit_ResendLands()
    {
        var session = new ClientSession(null!);
        const uint tail = 4;

        Assert.Equal(new uint[] { 1 }, RealIngestCommitBatch(session, Commit(1, 30, Direction8.E), serverTick: 10));
        Assert.Equal(new uint[] { 2 }, RealIngestCommitBatch(session, Commit(2, 33, Direction8.E), serverTick: 11));
        Assert.Equal(new uint[] { 3 }, RealIngestCommitBatch(session, Commit(3, 36, Direction8.E), serverTick: 12));
        Assert.Equal(3u, session.LastCommitSeq);

        // Keyup STOP intent at seq 5 (8x): advances the INTENT cursor only.
        var stopApplied = RealIngestMoveInput(session, StopIntent(tail + 1, redundancy: 8), serverTick: 13);
        Assert.Contains(tail + 1, stopApplied);
        Assert.Equal(tail + 1, session.LastMoveSeq);
        Assert.False(session.MoveIntentMoving); // stopped
        Assert.Equal(3u, session.LastCommitSeq); // commit cursor untouched by the stop intent — THE FIX

        // The tail re-send (seq 4) now LANDS: 4 > LastCommitSeq(3) → fresh. The first attempt consumes it; further
        // redundant re-sends are correctly deduped on the COMMIT cursor (4 <= 4) so the step fires exactly once.
        Assert.Equal(new uint[] { tail }, RealIngestCommitBatch(session, Commit(tail, 39, Direction8.E), serverTick: 14));
        Assert.Equal(tail, session.LastCommitSeq); // lead drained: commit cursor reached the tail
        for (var attempt = 0; attempt < 4; attempt++)
        {
            Assert.Empty(RealIngestCommitBatch(session, Commit(tail, 39, Direction8.E), serverTick: 15));
        }

        // The stop intent stuck (it was not re-armed by the late commit).
        Assert.False(session.MoveIntentMoving);
    }

    // ---- MANDATE 3: the mid-stream interleaved-intent case RECOVERS on the REAL decoupled ClientSession ---------
    [Fact]
    public void DecoupledCursor_InterleavedIntentMidStream_LaterWindowRecoversDroppedCommit()
    {
        var session = new ClientSession(null!);

        // Commit 1 lands. Commit 2 DROPS.
        Assert.Equal(new uint[] { 1 }, RealIngestCommitBatch(session, Commit(1, 30, Direction8.E), serverTick: 10));

        // Direction-change INTENT at seq 3 lands → advances LastMoveSeq only.
        Assert.Equal(new uint[] { 3 }, RealIngestMoveInput(session, new MoveInputMessage(3, true, Direction8.N, []), serverTick: 11));
        Assert.Equal(3u, session.LastMoveSeq);
        Assert.Equal(1u, session.LastCommitSeq); // commit cursor still at 1 — the intent did not burn it

        // The later commit (seq 4) re-carries the dropped commit 2 in its window. On the COMMIT cursor (=1), both
        // seq 2 and seq 4 are fresh → commit 2 is RECOVERED oldest-first, then 4. No strand.
        var recovery = new StepCommitBatchMessage(4, 42, Direction8.E,
        [
            new StepCommitWindowEntry(2, 9, Direction8.E), // seq 2 @ tick 33 — the dropped commit
        ]);
        var consumed = RealIngestCommitBatch(session, recovery, serverTick: 12);
        Assert.Equal(new uint[] { 2, 4 }, consumed); // commit 2 recovered from the window, then the head
        Assert.Equal(4u, session.LastCommitSeq);
    }

    // ---- REGRESSION: a stale/duplicate INTENT must STILL be rejected (decoupling did not weaken intent dedup) ----
    [Fact]
    public void DecoupledCursor_StaleOrDuplicateIntentStillRejected()
    {
        var session = new ClientSession(null!);

        Assert.True(session.TryUpdateMoveIntent(5, moving: true, Direction8.E, serverTick: 10));
        Assert.Equal(5u, session.LastMoveSeq);

        // Equal or lower intent seq is stale → rejected, state unchanged.
        Assert.False(session.TryUpdateMoveIntent(5, moving: false, Direction8.W, serverTick: 11));
        Assert.False(session.TryUpdateMoveIntent(4, moving: false, Direction8.W, serverTick: 11));
        Assert.True(session.MoveIntentMoving);
        Assert.Equal(Direction8.E, session.MoveIntentDirection);

        // And a stale/duplicate COMMIT is still rejected on its OWN cursor.
        Assert.True(session.TryConsumeCommitSequence(2, serverTick: 12));
        Assert.False(session.TryConsumeCommitSequence(2, serverTick: 13));
        Assert.False(session.TryConsumeCommitSequence(1, serverTick: 13));
        Assert.Equal(2u, session.LastCommitSeq);
    }

    // ---- REGRESSION: the two cursors advance INDEPENDENTLY (commit gaps where intents took numbers are fine) -----
    [Fact]
    public void DecoupledCursor_IntentAndCommitCursorsAdvanceIndependently()
    {
        var session = new ClientSession(null!);

        // Interleave: intent 1, commit 2, intent 3, commit 4 (the shared client counter; each stream sees gaps).
        Assert.True(session.TryUpdateMoveIntent(1, moving: true, Direction8.E, serverTick: 10));
        Assert.True(session.TryConsumeCommitSequence(2, serverTick: 11));
        Assert.True(session.TryUpdateMoveIntent(3, moving: true, Direction8.N, serverTick: 12));
        Assert.True(session.TryConsumeCommitSequence(4, serverTick: 13));

        Assert.Equal(3u, session.LastMoveSeq);   // highest intent seq
        Assert.Equal(4u, session.LastCommitSeq);  // highest commit seq — independent of the intent cursor

        // Commit dedup is on the COMMIT cursor only: seq 3 is <= commit 4 → stale (rejected), even though 3 also
        // happens to equal the intent cursor — the intent cursor is irrelevant to commit dedup.
        Assert.False(session.TryConsumeCommitSequence(3, serverTick: 14)); // 3 <= commit 4 → stale on commit cursor
        Assert.True(session.TryConsumeCommitSequence(5, serverTick: 14));   // 5 > commit 4 → fresh
        Assert.Equal(5u, session.LastCommitSeq);
        Assert.Equal(3u, session.LastMoveSeq);    // intent cursor untouched by commits
    }
}
