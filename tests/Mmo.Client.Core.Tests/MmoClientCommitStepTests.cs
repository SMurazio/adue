using Mmo.Shared.Domain;
using Mmo.Shared.Protocol;
using Xunit;

namespace Mmo.Client.Core.Tests;

// S103 commit-step on release at the MmoClient seam (model B / CosmeticLead, the default). Drive a real lead glide,
// release past the threshold, and assert: a commit is emitted (NET2: as a redundant StepCommitBatch, not a per-
// step StepCommitRequest) and the render does NOT snap back; an
// ACCEPTED confirm (the local entity reaches the committed tile) leaves the render there; a REJECTED confirm (the
// server steps without honouring the commit — RecipientStepSeq advances, tile unchanged) snaps the render back.
public sealed class MmoClientCommitStepTests
{
    private const uint LocalNetworkId = 9;
    private const int TickRate = 20;            // 50 ms/tick
    private const double TickMs = 1000d / TickRate;
    private const int StepCooldownMs = 150;     // 150 ms cadence

    [Fact]
    public void ReleasePastThreshold_EmitsCommit_AndRenderDoesNotSnapBack()
    {
        var spawn = new TileCoord(20, 20);
        var client = CreateLoggedInClientWithLocalEntity(spawn, out var outbound);

        GlidePastThreshold(client);

        // Release east: the lead is well past 0.7 onto (21,20), so a commit must be emitted and the render must NOT
        // snap back to spawn.
        client.SendMoveIntent(false, Direction8.E);

        // NET2: the release commit rides the redundant-unreliable StepCommitBatch (head = the committed step),
        // not the old reliable per-step StepCommitRequest.
        Assert.Empty(outbound.OfType<StepCommitRequestMessage>());
        var commit = Assert.Single(outbound.OfType<StepCommitBatchMessage>());
        Assert.Equal(Direction8.E, commit.Direction);

        // Render is still gliding toward (21,20), not snapped back to (20,20).
        var render = LocalRender(client, StepCooldownMs * 0.85);
        Assert.True(render.Position.X > spawn.X + 0.5,
            $"render must keep gliding to the committed tile, not snap back; was {render.Position.X}");
        // Confirmed tile (logic) is unchanged — the server hasn't acked yet.
        Assert.Equal(spawn, client.LocalTile);
    }

    [Fact]
    public void AcceptedCommit_LeavesRenderOnCommittedTile()
    {
        var spawn = new TileCoord(20, 20);
        var target = new TileCoord(21, 20);
        var client = CreateLoggedInClientWithLocalEntity(spawn, out _);

        GlidePastThreshold(client);
        client.SendMoveIntent(false, Direction8.E);

        // Server ACCEPTS: the next snapshot moves the local entity to the committed tile (RecipientStepSeq bumps).
        client.HandleMessageForTests(new WorldSnapshotMessage(
            ServerTick: 100,
            SnapshotSequence: 50,
            TotalEntities: 1,
            IsComplete: true,
            ChunkIndex: 0,
            ChunkCount: 1,
            Entities: new[] { new EntityStateSnapshot(LocalNetworkId, target, Direction8.E) },
            RecipientStepSeq: 1));

        Assert.Equal(target, client.LocalTile);

        // Render settles on the committed tile (no snap back). Sample well after the retarget tween completes.
        client.Poll(TimeSpan.FromMilliseconds(10 * TickMs));
        var render = LocalRender(client, 10 * TickMs);
        Assert.True(Math.Abs(render.Position.X - target.X) < 0.001,
            $"render should be on the committed tile; was {render.Position.X}");
    }

    [Fact]
    public void RejectedCommit_SnapsRenderBackToConfirmedTile()
    {
        var spawn = new TileCoord(20, 20);
        var client = CreateLoggedInClientWithLocalEntity(spawn, out _);

        GlidePastThreshold(client);
        client.SendMoveIntent(false, Direction8.E);

        // Server REJECTS the commit but DID process some step activity (RecipientStepSeq advances past the base 0)
        // while the local entity's confirmed tile stays at spawn (delta'd-out, unchanged). MmoClient must read that
        // as a reject and snap the render back to the confirmed (spawn) tile.
        for (var i = 0; i < 3; i++)
        {
            client.HandleMessageForTests(new WorldSnapshotMessage(
                ServerTick: (uint)(100 + i),
                SnapshotSequence: (uint)(50 + i),
                TotalEntities: 1,
                IsComplete: false,
                ChunkIndex: 0,
                ChunkCount: 1,
                Entities: new[] { new EntityStateSnapshot(777, new TileCoord(0, 0), Direction8.S) },
                RecipientStepSeq: 1)); // advanced past base 0 -> server stepped, but our tile is unchanged
            client.Poll(TimeSpan.FromMilliseconds((6 + i) * TickMs));
        }

        Assert.Equal(spawn, client.LocalTile);
        var render = LocalRender(client, 9 * TickMs);
        Assert.True(Math.Abs(render.Position.X - spawn.X) < 0.001 && Math.Abs(render.Position.Y - spawn.Y) < 0.001,
            $"render must have snapped back to the confirmed tile; was ({render.Position.X},{render.Position.Y})");
    }

    [Fact]
    public void BelowThreshold_DoesNotEmitCommit()
    {
        var spawn = new TileCoord(20, 20);
        var client = CreateLoggedInClientWithLocalEntity(spawn, out var outbound);
        client.SetCommitStepThreshold(0.95d); // very high -> an early release won't reach it

        // Arm the lead but release EARLY (small progress).
        client.SendMoveIntent(true, Direction8.E);
        client.Poll(TimeSpan.FromMilliseconds(0));
        client.Poll(TimeSpan.FromMilliseconds(StepCooldownMs * 0.2)); // ~0.2 onto the next tile
        client.SendMoveIntent(false, Direction8.E);

        Assert.Empty(outbound.OfType<StepCommitRequestMessage>());
        Assert.Empty(outbound.OfType<StepCommitBatchMessage>());
    }

    // Arms the lead and polls until the render has glided past the 0.7 commit threshold onto the next tile.
    private static void GlidePastThreshold(MmoClient client)
    {
        client.SendMoveIntent(true, Direction8.E);
        // Tick at t=0 to arm the lead, then sample late in the cadence so the glide is well past 0.7.
        client.Poll(TimeSpan.FromMilliseconds(0));
        client.Poll(TimeSpan.FromMilliseconds(StepCooldownMs * 0.85));
    }

    private static EntityRenderState LocalRender(MmoClient client, double nowMs)
    {
        return client.GetRenderStates(TimeSpan.FromMilliseconds(nowMs)).Single(r => r.NetworkId == LocalNetworkId);
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
            LocalNetworkId, characterId, EntityKind.Player, "Local", spawn, Direction8.S, StepCooldownMs: StepCooldownMs));

        Assert.Equal(LocalNetworkId, client.LocalNetworkId);
        Assert.Equal(spawn, client.LocalTile);
        // Model B (CosmeticLead) is the default; this is the mode commit-step lives in. Be explicit.
        client.RenderMode = MovementRenderMode.CosmeticLead;
        return client;
    }
}
