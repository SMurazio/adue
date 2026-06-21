using Mmo.Shared.Domain;
using Mmo.Shared.Protocol;
using Xunit;

namespace Mmo.Client.Core.Tests;

// UO1 client-driven render mode at the MmoClient seam. Selecting UoClientDriven routes the local player through the
// predictor (model A) AND (a) sends a MovementModeMessage(true) on enter / (false) on leave, and (b) emits the
// predicted accepted steps as commits (the server FOLLOWS the client). NET2: those commits now ride the
// redundant-unreliable StepCommitBatchMessage (head = the newest committed step of the Poll, plus a window of
// prior committed steps as deltas) instead of one reliable StepCommitRequest per step. These tests drive real
// prediction (held intent + Poll) over an open field and assert the emitted batch stream.
public sealed class MmoClientUoClientDrivenTests
{
    private const uint LocalNetworkId = 9;
    private const int TickRate = 20;            // 50 ms/tick
    private const double TickMs = 1000d / TickRate;
    private const int StepCooldownMs = 150;     // 150 ms cadence = 3 ticks

    // NET2: the committed-step sequences a batch carries, ascending — head plus window entries reconstructed
    // from their deltas (entrySeq = HeadSeq - SeqDelta). Mirrors the server's ExtractFreshStepCommits ordering.
    private static List<(uint Seq, Direction8 Direction)> BatchCommits(StepCommitBatchMessage batch)
    {
        var commits = new List<(uint Seq, Direction8 Direction)> { (batch.HeadSeq, batch.Direction) };
        foreach (var entry in batch.Window)
        {
            commits.Add((batch.HeadSeq - entry.SeqDelta, entry.Direction));
        }

        commits.Sort(static (a, b) => a.Seq.CompareTo(b.Seq));
        return commits;
    }

    // NET2: the DISTINCT committed steps across a stream of redundant batches, in seq order (the window repeats
    // earlier commits for loss recovery; the server dedupes them by sequence, so the test does too).
    private static List<(uint Seq, Direction8 Direction)> DistinctCommits(IEnumerable<StepCommitBatchMessage> batches)
    {
        var seen = new SortedDictionary<uint, Direction8>();
        foreach (var batch in batches)
        {
            foreach (var (seq, dir) in BatchCommits(batch))
            {
                seen[seq] = dir;
            }
        }

        return seen.Select(kv => (kv.Key, kv.Value)).ToList();
    }

    [Fact]
    public void EnteringUoMode_SendsClientDrivenTrue_LeavingSendsFalse()
    {
        var client = CreateLoggedInClientWithLocalEntity(new TileCoord(20, 20), out var outbound);

        client.SetMovementRenderMode(MovementRenderMode.UoClientDriven);
        var enter = Assert.Single(outbound.OfType<MovementModeMessage>());
        Assert.True(enter.ClientDriven);

        outbound.Clear();
        client.SetMovementRenderMode(MovementRenderMode.CosmeticLead);
        var leave = Assert.Single(outbound.OfType<MovementModeMessage>());
        Assert.False(leave.ClientDriven);
    }

    [Fact]
    public void UoMode_EmitsOneStepCommitPerPredictedStep()
    {
        var spawn = new TileCoord(20, 20);
        var client = CreateLoggedInClientWithLocalEntity(spawn, out var outbound);
        client.SetMovementRenderMode(MovementRenderMode.UoClientDriven);
        outbound.Clear(); // drop the mode-enter message; we only count commits below.

        // Hold east and poll across exactly 3 cadence boundaries (t=0, 150, 300 ms) -> 3 accepted predicted steps.
        client.SendMoveIntent(true, Direction8.E);
        client.Poll(TimeSpan.FromMilliseconds(0));
        client.Poll(TimeSpan.FromMilliseconds(StepCooldownMs));
        client.Poll(TimeSpan.FromMilliseconds(2 * StepCooldownMs));

        // NET2: the legacy per-step reliable StepCommitRequest is gone — commits ride the redundant batch.
        Assert.Empty(outbound.OfType<StepCommitRequestMessage>());

        // Three distinct committed steps (deduped across the redundant batch windows), all east.
        var commits = DistinctCommits(outbound.OfType<StepCommitBatchMessage>());
        Assert.Equal(3, commits.Count);
        Assert.All(commits, c => Assert.Equal(Direction8.E, c.Direction));

        // The predicted tile advanced three tiles east (the commits mirror the prediction one-for-one).
        Assert.Equal(new TileCoord(23, 20), client.PredictedLocalTile);
    }

    [Fact]
    public void UoMode_CommitSequencesAreStrictlyIncreasing_SharedWithMoveIntent()
    {
        var spawn = new TileCoord(20, 20);
        var client = CreateLoggedInClientWithLocalEntity(spawn, out var outbound);
        client.SetMovementRenderMode(MovementRenderMode.UoClientDriven);
        outbound.Clear();

        client.SendMoveIntent(true, Direction8.E);    // MoveIntent seq N
        client.Poll(TimeSpan.FromMilliseconds(0));     // commit seq N+1
        client.Poll(TimeSpan.FromMilliseconds(StepCooldownMs)); // commit seq N+2

        // Collect every sequenced outbound HEAD (MoveInput + commit batch) and assert the shared cursor is
        // strictly increasing with no collision between a MoveInput and a commit on the same number.
        var seqs = outbound
            .Select(m => m switch
            {
                // NET1 Stage 1: the held-input channel is the redundant MoveInputMessage (HeadSeq is the newest
                // input's sequence on the shared move cursor), not the old MoveIntentMessage.
                MoveInputMessage mi => (uint?)mi.HeadSeq,
                // NET2: the commit channel is the redundant StepCommitBatchMessage (HeadSeq is the newest
                // committed step's sequence on the same shared cursor), not the old per-step StepCommitRequest.
                StepCommitBatchMessage sc => sc.HeadSeq,
                _ => null,
            })
            .Where(s => s.HasValue)
            .Select(s => s!.Value)
            .ToList();

        Assert.True(seqs.Count >= 3);
        for (var i = 1; i < seqs.Count; i++)
        {
            Assert.True(seqs[i] > seqs[i - 1], $"sequence must strictly increase; {seqs[i]} <= {seqs[i - 1]}");
        }
    }

    [Fact]
    public void NonUoMode_EmitsNoCommitsFromPolling_AndNoModeMessage()
    {
        var spawn = new TileCoord(20, 20);
        var client = CreateLoggedInClientWithLocalEntity(spawn, out var outbound);
        // RENDER1: CosmeticLead is the only non-UO mode now (Predicted was dropped). It must not stream commits
        // from Poll, nor send a movement-mode message. (Default boot is UoClientDriven, so pin CosmeticLead first.)
        client.SetMovementRenderMode(MovementRenderMode.CosmeticLead);
        outbound.Clear(); // drop the UO->Cosmetic transition's MovementModeMessage(clientDriven:false).

        client.SendMoveIntent(true, Direction8.E);
        client.Poll(TimeSpan.FromMilliseconds(0));
        client.Poll(TimeSpan.FromMilliseconds(StepCooldownMs));

        Assert.Empty(outbound.OfType<MovementModeMessage>());
        Assert.Empty(outbound.OfType<StepCommitRequestMessage>());
        Assert.Empty(outbound.OfType<StepCommitBatchMessage>());
    }

    [Fact]
    public void UoMode_StopOnReversalOn_180Flip_SuppressesTheReverseCommitForOneBeat()
    {
        // UO4 at the client seam: in UoClientDriven with "Stop on reversal" ON, a 180° flip while moving must NOT
        // emit a reversed commit on the very next beat (the settle) — the server, which follows commits, then does
        // not step the reverse either. The beat after resumes and emits the new (W) commit.
        var spawn = new TileCoord(20, 20);
        var client = CreateLoggedInClientWithLocalEntity(spawn, out var outbound);
        client.SetMovementRenderMode(MovementRenderMode.UoClientDriven);
        client.SetStopOnReversal(true);
        outbound.Clear();

        // Travel E for two steps, then flip to W (180°).
        client.SendMoveIntent(true, Direction8.E);
        client.Poll(TimeSpan.FromMilliseconds(0));                 // E commit (step to 21,20)
        client.Poll(TimeSpan.FromMilliseconds(StepCooldownMs));    // E commit (step to 22,20)
        Assert.Equal(new TileCoord(22, 20), client.PredictedLocalTile);

        outbound.Clear();
        client.SendMoveIntent(true, Direction8.W);                 // 180° reversal -> arm a settle

        // The next beat is the SETTLE: no commit emitted (no batch at all), no tile move.
        client.Poll(TimeSpan.FromMilliseconds(2 * StepCooldownMs));
        Assert.Empty(outbound.OfType<StepCommitBatchMessage>());
        Assert.Equal(new TileCoord(22, 20), client.PredictedLocalTile);

        // The following beat resumes W: one batch whose HEAD is the new W commit, stepping to (21,20). (The
        // batch's window may still carry the earlier E commits for loss recovery; the HEAD is what is new.)
        client.Poll(TimeSpan.FromMilliseconds(3 * StepCooldownMs));
        var batch = Assert.Single(outbound.OfType<StepCommitBatchMessage>());
        Assert.Equal(Direction8.W, batch.Direction);
        Assert.Equal(new TileCoord(21, 20), client.PredictedLocalTile);
    }

    private static MmoClient CreateLoggedInClientWithLocalEntity(TileCoord spawn, out List<IProtocolMessage> outbound)
    {
        outbound = [];
        var captured = outbound;
        var client = new MmoClient(
            new ClientConnectionOptions("127.0.0.1", 1, "test", "account", "display"),
            new ClientMovementTrace(false, null));
        client.OutboundSinkForTests = (message, _) => captured.Add(message);

        var characterId = Guid.NewGuid();
        client.HandleMessageForTests(new ServerHelloMessage("test", ProtocolCodec.Version, TickRate, StepCooldownMs, 30));
        var zone = new ZoneModel("zone", 64, 64, 0, 1);
        client.HandleMessageForTests(new ZoneInfoMessage("zone", 64, 64, 0, 1, zone.ContentHash));
        client.HandleMessageForTests(new LoginResultMessage(true, characterId, "Local", ClientRole.Player, spawn, ""));
        client.HandleMessageForTests(new EntitySpawnMessage(
            LocalNetworkId, characterId, EntityKind.Player, "Local", spawn, Direction8.E, StepCooldownMs: StepCooldownMs));

        Assert.Equal(LocalNetworkId, client.LocalNetworkId);
        Assert.Equal(spawn, client.LocalTile);

        // RENDER1: the client now BOOTS into UoClientDriven (the new default). These tests assert the UO ENTER
        // transition (MovementModeMessage(true) + commit stream), so start each from the non-UO baseline
        // (CosmeticLead) and clear the transition's outbound so a later SetMovementRenderMode(UoClientDriven) is a
        // real enter (not a no-op). Mirrors the pre-RENDER1 default and keeps every UO-transition assertion intact.
        client.SetMovementRenderMode(MovementRenderMode.CosmeticLead);
        outbound.Clear();
        return client;
    }
}
