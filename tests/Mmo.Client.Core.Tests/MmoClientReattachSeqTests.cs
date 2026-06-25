using System.Collections.Generic;
using System.Linq;
using Mmo.Client.Core;
using Mmo.Server.Configuration;
using Mmo.Server.Runtime;
using Mmo.Shared.Domain;
using Mmo.Shared.Protocol;
using Xunit;

namespace Mmo.Client.Core.Tests;

// CONTINUOUS MIGRATION (Phase 4a) — the RE-ATTACH freeze guard (Phase-4 review Finding A, BLOCK).
//
// THE BUG: the movement input-seq was minted by TWO disjoint counters — a fresh per-predictor _nextInputSeq (started
// at 0 on every EnsurePredictor) and a separate _preSpawnMoveSeq for the prediction-off path. The SERVER gate is
// `inputSeq <= _lastInputSeq -> REJECT`. So after ANY mid-session re-attach (F5 "Prediction" toggle, death/respawn,
// AOI re-entry — all null the predictor then rebuild it) the new counter restarted at 0 and minted 1,2,3… all <= the
// server's already-high acked cursor N → the server rejected EVERY MoveIntent until the local counter climbed back
// past N → a multi-second rubberband/freeze proportional to session length. First spawn (N=0) was fine, which is why
// the timing-faithful harness (a single long-lived predictor, never re-attached) never caught it.
//
// THE FIX: a SINGLE persistent monotonic high-water on MmoClient that survives predictor re-attach AND the
// prediction on/off toggle. EnsurePredictor SEEDS each fresh predictor from it; both the predictor path and the
// prediction-off path mint from it. The invariant these tests pin: across re-attach and toggle, the NEXT sent seq is
// STRICTLY GREATER than every previously-sent seq (hence > the server's acked cursor) → never rejected as a stale dup.
public sealed class MmoClientReattachSeqTests
{
    [Fact]
    public void SeqStaysAboveServerCursor_AcrossPredictionToggleReattach()
    {
        using var client = CreateAttachedLocalPlayer(out var outbound, out var localNetworkId, out _);

        // Walk a while: send several inputs so the seq climbs. The server acks them, advancing its cursor to N.
        uint lastSeqSent = 0;
        for (var i = 0; i < 8; i++)
        {
            lastSeqSent = client.PredictAndSendMove(1f, 0f, 0.05f);
        }

        // The server's authoritative cursor is now lastSeqSent (it integrated and acked every input). Drive that home
        // via a snapshot whose header LastInputSeq == lastSeqSent (exactly what the wire carries).
        var n = lastSeqSent;
        Assert.True(n > 0, "precondition: inputs should have minted a non-zero seq");
        client.HandleMessageForTests(SnapshotWithInputSeq(10, localNetworkId, 6, 5, lastInputSeq: n));

        // RE-ATTACH via the live F5 path: prediction OFF (nulls the predictor) then ON (EnsurePredictor rebuilds a
        // fresh predictor). The OLD bug: the fresh predictor's counter restarts at 0.
        client.PredictionEnabled = false;
        client.PredictionEnabled = true;

        outbound.Clear();
        var nextSeq = client.PredictAndSendMove(1f, 0f, 0.05f);

        // THE INVARIANT: the next minted seq is STRICTLY above the server's cursor N, so a server whose
        // _lastInputSeq == N ACCEPTS it (inputSeq <= _lastInputSeq is FALSE) rather than rejecting it as a dup.
        Assert.True(nextSeq > n, $"re-attach reset the seq below the server cursor: next={nextSeq}, serverCursor={n}");
        var sent = Assert.Single(outbound.OfType<MoveIntentMessage>());
        Assert.Equal(nextSeq, sent.InputSeq);
    }

    [Fact]
    public void SeqStaysAboveServerCursor_AcrossRespawnReattach()
    {
        using var client = CreateAttachedLocalPlayer(out var outbound, out var localNetworkId, out var characterId);

        uint lastSeqSent = 0;
        for (var i = 0; i < 8; i++)
        {
            lastSeqSent = client.PredictAndSendMove(1f, 0f, 0.05f);
        }

        var n = lastSeqSent;
        Assert.True(n > 0);

        // DESPAWN the local entity (death / AOI exit) — ClearLocalEntity nulls the predictor and LocalNetworkId.
        client.HandleMessageForTests(new EntityDespawnMessage(0, localNetworkId));
        Assert.Null(client.LocalNetworkId);

        // RESPAWN: a fresh EntitySpawn re-creates the local entity and EnsurePredictor rebuilds the predictor.
        client.HandleMessageForTests(new EntitySpawnMessage(localNetworkId, characterId, EntityKind.Player, "Local", new TileCoord(5, 5), Direction8.S, StepCooldownMs: 50));

        outbound.Clear();
        var nextSeq = client.PredictAndSendMove(1f, 0f, 0.05f);

        Assert.True(nextSeq > n, $"respawn reset the seq below the server cursor: next={nextSeq}, serverCursor={n}");
        var sent = Assert.Single(outbound.OfType<MoveIntentMessage>());
        Assert.Equal(nextSeq, sent.InputSeq);
    }

    [Fact]
    public void SeqStaysMonotonic_AcrossPrePredictionToggleThenAttach()
    {
        // The prediction-OFF / pre-spawn path used to mint from a SEPARATE counter. Verify a toggle ON (which attaches
        // a predictor) continues strictly above the seqs the OFF path already sent — no overlap with the server cursor.
        using var client = CreateAttachedLocalPlayer(out var outbound, out var localNetworkId, out _);

        // Disable prediction (no predictor) and send via the off-path; the seq must still climb monotonically.
        client.PredictionEnabled = false;
        uint offPathSeq = 0;
        for (var i = 0; i < 5; i++)
        {
            offPathSeq = client.PredictAndSendMove(1f, 0f, 0.05f);
        }

        Assert.True(offPathSeq > 0);

        // Re-enable prediction → EnsurePredictor attaches, seeded from the high-water the off-path advanced.
        client.PredictionEnabled = true;
        outbound.Clear();
        var nextSeq = client.PredictAndSendMove(1f, 0f, 0.05f);

        Assert.True(nextSeq > offPathSeq, $"predictor attach reset the seq below the off-path cursor: next={nextSeq}, offPath={offPathSeq}");
        Assert.Equal(nextSeq, Assert.Single(outbound.OfType<MoveIntentMessage>()).InputSeq);
    }

    // Build a client driven to the point where a continuous predictor is attached for the LOCAL player: ServerHello
    // (radius), ZoneInfo (blocked map), LoginResult (character id), EntitySpawn (local entity), and a first snapshot.
    // No socket — HandleMessageForTests drives the lifecycle and OutboundSinkForTests captures the wire MoveIntents.
    private static MmoClient CreateAttachedLocalPlayer(out List<IProtocolMessage> outbound, out uint localNetworkId, out System.Guid characterId)
    {
        outbound = [];
        var captured = outbound;
        var client = new MmoClient(new ClientConnectionOptions("127.0.0.1", 1, "test", "account", "display"));
        client.OutboundSinkForTests = (message, _) => captured.Add(message);

        characterId = System.Guid.NewGuid();
        localNetworkId = 9u;

        // A real generated zone so EnsurePredictor's blocked-map / hash gate is satisfied exactly as in production.
        var zone = Zone.CreateGenerated(64, 64, 0, TerrainGenerator.CurrentGenVersion, SpawnDistribution.Clustered);
        var serverHash = TerrainGenerator.ContentHash(zone.Width, zone.Height, zone.Seed, zone.GenVersion);

        // 50ms cooldown is tick-aligned at 20Hz → a clean derived predictor speed; body radius 0.5 like production.
        client.HandleMessageForTests(new ServerHelloMessage("test", ProtocolCodec.Version, 20, 50, 30, 0.5f));
        client.HandleMessageForTests(new ZoneInfoMessage(zone.Id, zone.Width, zone.Height, zone.Seed, zone.GenVersion, serverHash));
        client.HandleMessageForTests(new LoginResultMessage(true, characterId, "Local", ClientRole.Player, new TileCoord(5, 5), ""));
        client.HandleMessageForTests(new EntitySpawnMessage(localNetworkId, characterId, EntityKind.Player, "Local", new TileCoord(5, 5), Direction8.S, StepCooldownMs: 50));
        client.HandleMessageForTests(SnapshotWithInputSeq(1, localNetworkId, 5, 5, lastInputSeq: 0));

        return client;
    }

    // A single-chunk full snapshot carrying the local entity at (x,y) and a header LastInputSeq — the server's acked
    // input cursor (the value the predictor's reconcile and the server's dedup gate both key on).
    private static WorldSnapshotMessage SnapshotWithInputSeq(uint sequence, uint localNetworkId, int x, int y, uint lastInputSeq)
    {
        return new WorldSnapshotMessage(
            ServerTick: 10,
            SnapshotSequence: sequence,
            TotalEntities: 1,
            IsComplete: true,
            ChunkIndex: 0,
            ChunkCount: 1,
            Entities: [new EntityStateSnapshot(localNetworkId, WorldVector.FromTile(x, y), Direction8.E)],
            RecipientStepSeq: 0,
            LastInputSeq: lastInputSeq);
    }
}
