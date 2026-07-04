using System.Collections.Generic;
using System.Linq;
using Mmo.Client.Core;
using Mmo.Server.Configuration;
using Mmo.Server.Runtime;
using Mmo.Shared.Domain;
using Mmo.Shared.Domain.Population;
using Mmo.Shared.Protocol;
using Xunit;

namespace Mmo.Client.Core.Tests;

// CONTINUOUS MIGRATION (Phase 4a) — the RE-ATTACH freeze guard (Phase-4 review Finding A, BLOCK).
//
// THE BUG: the movement input-seq was minted by TWO disjoint counters — a fresh per-predictor _nextInputSeq (started
// at 0 on every EnsurePredictor) and a separate _preSpawnMoveSeq for the pre-spawn path. The SERVER gate is
// `inputSeq <= _lastInputSeq -> REJECT`. So after ANY mid-session re-attach (death/respawn, AOI re-entry — both null
// the predictor then rebuild it) the new counter restarted at 0 and minted 1,2,3… all <= the server's already-high
// acked cursor N → the server rejected EVERY MoveIntent until the local counter climbed back past N → a multi-second
// rubberband/freeze proportional to session length. First spawn (N=0) was fine, which is why the timing-faithful
// harness (a single long-lived predictor, never re-attached) never caught it.
//
// THE FIX: a SINGLE persistent monotonic high-water on MmoClient that survives predictor re-attach. EnsurePredictor
// SEEDS each fresh predictor from it; both the predictor path and the pre-spawn path mint from it. The invariant
// these tests pin: across re-attach, the NEXT sent seq is STRICTLY GREATER than every previously-sent seq (hence >
// the server's acked cursor) → never rejected as a stale dup. (The continuous migration removed the dev-only
// prediction on/off A/B toggle that was a THIRD re-attach vector — the respawn vector below covers the same guard.)
public sealed class MmoClientReattachSeqTests
{
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
    public void SeqStaysMonotonic_AcrossPreSpawnThenPredictorAttach()
    {
        // The PRE-SPAWN path (no predictor attached yet) used to mint from a SEPARATE counter. Verify that once the
        // local entity spawns and EnsurePredictor attaches a predictor, it continues STRICTLY above the seqs the
        // pre-spawn path already sent — no overlap with the server cursor.
        var outbound = new List<IProtocolMessage>();
        var captured = outbound;
        using var client = new MmoClient(new ClientConnectionOptions("127.0.0.1", 1, "test", "account", "display"));
        client.OutboundSinkForTests = (message, _) => captured.Add(message);

        var characterId = System.Guid.NewGuid();
        var localNetworkId = 9u;
        var zone = Zone.CreateGenerated(64, 64, 0, 1, SpawnDistribution.Clustered);
        var serverHash = TerrainGenerator.ContentHash(zone.Width, zone.Height, zone.Seed, zone.GenVersion);

        // Hello + zone + login land, but the local EntitySpawn has NOT — so no predictor is attached yet and sends go
        // through the pre-spawn fallback counter.
        client.HandleMessageForTests(new ServerHelloMessage("test", ProtocolCodec.Version, 20, 50, 30, 0.5f));
        // NODE-FIELD N2: genVersion 1 (procedural) has no authored map to scatter from, so both sides agree on
        // the trivial empty catalogue's hash.
        client.HandleMessageForTests(new ZoneInfoMessage(zone.Id, zone.Width, zone.Height, zone.Seed, zone.GenVersion, serverHash, NodeCatalog.Empty().CatalogHash));
        client.HandleMessageForTests(new LoginResultMessage(true, characterId, "Local", ClientRole.Player, new TileCoord(5, 5), ""));

        uint preSpawnSeq = 0;
        for (var i = 0; i < 5; i++)
        {
            preSpawnSeq = client.PredictAndSendMove(1f, 0f, 0.05f);
        }

        Assert.True(preSpawnSeq > 0);

        // The local entity spawns → EnsurePredictor attaches, seeded from the high-water the pre-spawn path advanced.
        client.HandleMessageForTests(new EntitySpawnMessage(localNetworkId, characterId, EntityKind.Player, "Local", new TileCoord(5, 5), Direction8.S, StepCooldownMs: 50));
        outbound.Clear();
        var nextSeq = client.PredictAndSendMove(1f, 0f, 0.05f);

        Assert.True(nextSeq > preSpawnSeq, $"predictor attach reset the seq below the pre-spawn cursor: next={nextSeq}, preSpawn={preSpawnSeq}");
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
        var zone = Zone.CreateGenerated(64, 64, 0, 1, SpawnDistribution.Clustered);
        var serverHash = TerrainGenerator.ContentHash(zone.Width, zone.Height, zone.Seed, zone.GenVersion);

        // 50ms cooldown is tick-aligned at 20Hz → a clean derived predictor speed; body radius 0.5 like production.
        client.HandleMessageForTests(new ServerHelloMessage("test", ProtocolCodec.Version, 20, 50, 30, 0.5f));
        // NODE-FIELD N2: genVersion 1 (procedural) has no authored map to scatter from, so both sides agree on
        // the trivial empty catalogue's hash.
        client.HandleMessageForTests(new ZoneInfoMessage(zone.Id, zone.Width, zone.Height, zone.Seed, zone.GenVersion, serverHash, NodeCatalog.Empty().CatalogHash));
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
