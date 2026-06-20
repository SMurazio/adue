using System.Linq;
using Mmo.Shared.Domain;
using Mmo.Shared.Protocol;
using Xunit;

namespace Mmo.Client.Core.Tests;

// S84 — the local player must reconcile on EVERY snapshot, even when it is delta'd out of the entity list.
//
// The server delta-compresses: it re-sends an entity only while its StateRevision changes. So while the local
// player MOVES its tile changes each step and it rides every snapshot (reconcile runs), but the instant it goes
// IDLE the server stops re-sending its tile (delta'd out) and the pre-S84 client stopped reconciling the local
// player at all — any over-prediction left by a turn/step spam latched at rest and never closed ("static, gap
// won't close"). S76 already rides the recipient-scoped RecipientStepSeq on EVERY snapshot header (real-delta
// AND keep-alive) for exactly this; S84 makes ApplySnapshot consume it for the delta'd-out local player.
//
// These tests exercise the REAL MmoClient.ApplySnapshot delta'd-out routing (snapshots that DO carry the header
// RecipientStepSeq + ServerTick but DO NOT contain the local entity) — NOT a direct LocalPlayerPredictor.Reconcile
// call (that direct path is the blind spot that hid this).
public sealed class MmoClientDeltadOutReconcileTests
{
    private const uint LocalNetworkId = 9;
    private const int TickRate = 20;          // 50 ms/tick
    private const double TickMs = 1000d / TickRate;
    private const int StepCooldownMs = 150;    // 3 ticks -> 150 ms cadence

    [Fact]
    public void DeltadOutSnapshot_ReconcilesIdleLocalPlayer_ConvergesToConfirmedTile()
    {
        var spawn = new TileCoord(20, 20);
        var client = CreateLoggedInClientWithLocalEntity(spawn, out _);

        // Drive a real over-prediction at the MmoClient seam: hold a direction and Poll the predictor forward on
        // the tick grid WITHOUT any snapshot confirming the local entity moving, so the predicted tile runs ahead
        // of the confirmed (spawn) tile — exactly the turn/step-spam over-prediction the latch leaves behind.
        client.SendMoveIntent(true, Direction8.E);
        for (var tick = 0; tick <= 6; tick++)
        {
            client.Poll(TimeSpan.FromMilliseconds(tick * TickMs));
        }

        // Release the key (the server will never confirm the over-predicted steps — their intents arrived after
        // the release), then keep polling so no further predicted steps fire.
        client.SendMoveIntent(false, Direction8.E);
        client.Poll(TimeSpan.FromMilliseconds(7 * TickMs));

        // The prediction is genuinely ahead of the confirmed tile, and the confirmed tile is still the spawn tile
        // (no snapshot has moved the local entity).
        Assert.Equal(spawn, client.LocalTile);
        Assert.NotEqual(spawn, client.PredictedLocalTile);
        var overPredictedTile = client.PredictedLocalTile!.Value;

        // Now feed DELTA'D-OUT snapshots: each carries the header RecipientStepSeq (0 — the server accepted no
        // tile moves; the player is at rest) and an advancing ServerTick, but DOES NOT contain the local entity
        // (a different, remote entity keeps the payload non-empty and delta-shaped). Pre-S84 these never touched
        // the local player and the over-prediction stayed stuck; S84 reconciles it down to the confirmed tile.
        var seq = 10u;
        for (var i = 0; i < 8; i++)
        {
            var serverTick = (uint)(100 + i);
            var wallMs = TimeSpan.FromMilliseconds(8 * TickMs + i * TickMs);
            client.Poll(wallMs);
            client.HandleMessageForTests(new WorldSnapshotMessage(
                ServerTick: serverTick,
                SnapshotSequence: seq++,
                TotalEntities: 1,
                IsComplete: false,          // a delta snapshot (not a full re-baseline)
                ChunkIndex: 0,
                ChunkCount: 1,
                Entities: new[] { new EntityStateSnapshot(777, new TileCoord(0, 0), Direction8.S) },
                RecipientStepSeq: 0));      // server's count of OUR accepted moves: 0 (at rest)
            client.Poll(wallMs);
        }

        // Fails-before (pre-S84): the predicted tile stays at the over-predicted tile — the latch.
        // Passes-after (S84): the delta'd-out reconcile re-anchors the prediction down to the confirmed tile.
        Assert.NotEqual(overPredictedTile, client.PredictedLocalTile);
        Assert.Equal(spawn, client.PredictedLocalTile);
        Assert.Equal(client.LocalTile, client.PredictedLocalTile);
    }

    [Fact]
    public void DeltadOutSnapshot_DoesNotFabricateAMove_WhenAlreadyConverged()
    {
        // A delta'd-out snapshot while the prediction already agrees with the confirmed tile must be a no-op for
        // the position (no fabricated tile change) — it only keeps calibration/reconcile ticking. Guards against
        // the delta'd-out path nudging an at-rest, already-correct local player.
        var spawn = new TileCoord(15, 15);
        var client = CreateLoggedInClientWithLocalEntity(spawn, out _);

        // Attach the predictor (it attaches lazily on the first move intent) without advancing it: press then
        // release at t=0 with no Poll in between, so no step fires and the prediction sits on the confirmed tile.
        client.SendMoveIntent(true, Direction8.E);
        client.SendMoveIntent(false, Direction8.E);
        client.Poll(TimeSpan.Zero);
        Assert.Equal(spawn, client.PredictedLocalTile);

        var seq = 5u;
        for (var i = 0; i < 5; i++)
        {
            client.HandleMessageForTests(new WorldSnapshotMessage(
                ServerTick: (uint)(200 + i),
                SnapshotSequence: seq++,
                TotalEntities: 1,
                IsComplete: false,
                ChunkIndex: 0,
                ChunkCount: 1,
                Entities: new[] { new EntityStateSnapshot(777, new TileCoord(0, 0), Direction8.S) },
                RecipientStepSeq: 0));
            client.Poll(TimeSpan.FromMilliseconds(i * TickMs));
        }

        Assert.Equal(spawn, client.LocalTile);
        Assert.Equal(spawn, client.PredictedLocalTile);
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
        // ServerHello first so the predictor seeds the real tick interval (50 ms) + cadence (150 ms) when it
        // attaches; ZoneInfo establishes the blocked map (EnsurePredictor needs a Zone); Login + EntitySpawn
        // establish the local entity (LocalNetworkId). All three prerequisites must be present before the first
        // SendMoveIntent for EnsurePredictor to attach the predictor.
        client.HandleMessageForTests(new ServerHelloMessage("test", ProtocolCodec.Version, TickRate, StepCooldownMs, 30));
        var zone = new ZoneModel("zone", 64, 64, 0, 1);
        client.HandleMessageForTests(new ZoneInfoMessage("zone", 64, 64, 0, 1, zone.ContentHash));
        client.HandleMessageForTests(new LoginResultMessage(true, characterId, "Local", ClientRole.Player, spawn, ""));
        client.HandleMessageForTests(new EntitySpawnMessage(
            LocalNetworkId, characterId, EntityKind.Player, "Local", spawn, Direction8.S, StepCooldownMs: StepCooldownMs));

        Assert.Equal(LocalNetworkId, client.LocalNetworkId);
        Assert.Equal(spawn, client.LocalTile);
        Assert.NotNull(client.Zone);
        // S92: model B (cosmetic lead) is now the default render mode, which routes the local player via the
        // cosmetic driver (no PredictedLocalTile). These tests exercise model A's predictor reconcile at the
        // MmoClient seam, so pin the mode to Predicted explicitly.
        client.RenderMode = MovementRenderMode.Predicted;
        return client;
    }
}
