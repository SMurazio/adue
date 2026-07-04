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

// N (entity-collision walk anim): EntityRenderState.Moving is the coherent MOVING signal the player walk/idle visuals
// key off — REMOTE entities from their replicated Velocity (~0 when blocked, tangential when sliding), the LOCAL
// player from the predictor's resolved velocity. It replaces the old per-frame render-delta detection that kept the
// "walk" loop latched on the sub-pixel jitter left when pushing into a body. These pin the Core computation headlessly
// (the animation itself is human-feel-tested): a blocked entity idles like a flat wall, a walking/sliding one animates.
public sealed class EntityRenderStateMovingTests
{
    private static readonly TimeSpan Now = TimeSpan.FromMilliseconds(500);

    [Fact]
    public void RemoteEntity_ZeroVelocity_IsNotMoving()
    {
        using var client = CreateClient(out _);

        // A remote entity at rest (Velocity Zero — the default) reads Moving=false → the visual idles.
        client.HandleMessageForTests(Snapshot(1, new EntityStateSnapshot(7, WorldVector.FromTile(5, 5), Direction8.S)));

        Assert.False(LocalOrId(client, 7).Moving);
    }

    [Fact]
    public void RemoteEntity_NonZeroVelocity_IsMoving()
    {
        using var client = CreateClient(out _);

        // A remote entity walking (replicated velocity 3 u/s east) reads Moving=true → the visual walks.
        client.HandleMessageForTests(Snapshot(1,
            new EntityStateSnapshot(7, WorldVector.FromTile(5, 5), Direction8.S, Velocity: new WorldVector(3d, 0d))));

        Assert.True(LocalOrId(client, 7).Moving);
    }

    [Fact]
    public void RemoteEntity_TinyVelocityBelowEpsilon_IsNotMoving()
    {
        using var client = CreateClient(out _);

        // A velocity below the ~0.5 u/s idle epsilon (0.3 u/s → 0.09 < 0.25 squared) reads idle — sub-threshold drift
        // does not flap the walk loop.
        client.HandleMessageForTests(Snapshot(1,
            new EntityStateSnapshot(7, WorldVector.FromTile(5, 5), Direction8.S, Velocity: new WorldVector(0.3d, 0d))));

        Assert.False(LocalOrId(client, 7).Moving);
    }

    [Fact]
    public void LocalPlayer_WalkingOnOpenGround_IsMoving()
    {
        using var client = CreateAttachedLocalPlayer(out _, out var localNetworkId);

        // Drive east on open ground (the spawn neighbourhood is verified open): the predictor's resolved velocity is the
        // full walk speed → Moving=true.
        for (var i = 0; i < 4; i++)
        {
            client.PredictAndSendMove(1f, 0f, 0.05f);
        }

        Assert.True(LocalOrId(client, localNetworkId).Moving);
    }

    [Fact]
    public void LocalPlayer_NoInput_IsNotMoving()
    {
        using var client = CreateAttachedLocalPlayer(out _, out var localNetworkId);

        client.PredictAndSendMove(1f, 0f, 0.05f); // move once
        Assert.True(LocalOrId(client, localNetworkId).Moving);

        client.PredictAndSendMove(0f, 0f, 0.05f); // release input → resolved velocity 0 → idle

        Assert.False(LocalOrId(client, localNetworkId).Moving);
    }

    [Fact]
    public void LocalPlayer_BlockedHeadOnByAMonster_IsNotMoving()
    {
        using var client = CreateAttachedLocalPlayer(out _, out var localNetworkId);

        // A stationary monster one tile east of the local player (centres 1.0 apart == the 0.5+0.5 radius sum, so they
        // are already in contact). Shoving straight east is fully blocked by the monster body → the predictor's resolved
        // velocity is ~0 → Moving=false → the avatar idles, exactly like being pinned against a flat wall.
        client.HandleMessageForTests(new EntitySpawnMessage(
            50, Guid.NewGuid(), EntityKind.Monster, "M50", new TileCoord(6, 5), Direction8.S, StepCooldownMs: 140));

        for (var i = 0; i < 8; i++)
        {
            client.PredictAndSendMove(1f, 0f, 0.05f); // shove east into the monster
        }

        Assert.False(LocalOrId(client, localNetworkId).Moving);
    }

    private EntityRenderState LocalOrId(MmoClient client, uint networkId) =>
        client.GetRenderStates(Now).Single(r => r.NetworkId == networkId);

    private static WorldSnapshotMessage Snapshot(uint sequence, params EntityStateSnapshot[] entities) =>
        new(10, sequence, entities.Length, true, 0, 1, entities);

    private static MmoClient CreateClient(out List<IProtocolMessage> outbound)
    {
        outbound = [];
        var captured = outbound;
        var client = new MmoClient(new ClientConnectionOptions("127.0.0.1", 1, "test", "account", "display"));
        client.OutboundSinkForTests = (message, _) => captured.Add(message);
        return client;
    }

    // Drive a client to an ATTACHED local predictor: ServerHello (radius), ZoneInfo (blocked map — the seed-0 clustered
    // 64x64 zone whose spawn neighbourhood is open), LoginResult, EntitySpawn, first snapshot. Mirrors the reattach-seq
    // harness. No socket — HandleMessageForTests drives the lifecycle.
    private static MmoClient CreateAttachedLocalPlayer(out List<IProtocolMessage> outbound, out uint localNetworkId)
    {
        var client = CreateClient(out outbound);
        var characterId = Guid.NewGuid();
        localNetworkId = 9u;

        var zone = Zone.CreateGenerated(64, 64, 0, 1, SpawnDistribution.Clustered);
        var serverHash = TerrainGenerator.ContentHash(zone.Width, zone.Height, zone.Seed, zone.GenVersion);

        client.HandleMessageForTests(new ServerHelloMessage("test", ProtocolCodec.Version, 20, 50, 30, 0.5f));
        // NODE-FIELD N2: genVersion 1 (procedural) has no authored map to scatter from, so both sides agree on
        // the trivial empty catalogue's hash.
        client.HandleMessageForTests(new ZoneInfoMessage(zone.Id, zone.Width, zone.Height, zone.Seed, zone.GenVersion, serverHash, NodeCatalog.Empty().CatalogHash));
        client.HandleMessageForTests(new LoginResultMessage(true, characterId, "Local", ClientRole.Player, new TileCoord(5, 5), ""));
        client.HandleMessageForTests(new EntitySpawnMessage(localNetworkId, characterId, EntityKind.Player, "Local", new TileCoord(5, 5), Direction8.S, StepCooldownMs: 50));
        client.HandleMessageForTests(new WorldSnapshotMessage(10, 1, 1, true, 0, 1,
            new[] { new EntityStateSnapshot(localNetworkId, WorldVector.FromTile(5, 5), Direction8.S) }));

        return client;
    }
}
