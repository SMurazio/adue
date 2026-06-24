using LiteNetLib;
using Mmo.Shared.Domain;
using Mmo.Shared.Protocol;
using Xunit;

namespace Mmo.Client.Core.Tests;

// MONSTER-HOP routing seam (client-only): EntityKind.Monster renders through the hop driver (rest-on-latest-tile +
// arc) instead of the buffered TileInterpolator. These tests assert the SEAM through the live MmoClient: a Monster's
// render sits on its latest confirmed (authoritative) tile while a Player at the same cadence still renders behind it
// (the buffered playout lag), and the catch-up-to-newest holds. The per-interpolator math is covered by
// MonsterHopInterpolatorTests; here we prove only Monster is routed to it and everything else is unchanged.
public sealed class MmoClientMonsterHopTests
{
    // A Monster rests EXACTLY on its latest confirmed tile (on the cyan server marker) — no buffered-past lag, which
    // is what made melee miss. A Player at the SAME tile/cadence renders BEHIND its confirmed tile (the buffered
    // interpolator), proving the per-kind routing: only Monster takes the hop path.
    [Fact]
    public void MonsterRendersOnLatestTileWhilePlayerStaysBuffered()
    {
        using var client = CreateClient(out _);
        client.HandleMessageForTests(new ServerHelloMessage("test", ProtocolCodec.Version, 20, 140, 30));

        // Spawn a Monster and a Player, both at the origin, both at the default cadence.
        client.HandleMessageForTests(new EntitySpawnMessage(1, Guid.NewGuid(), EntityKind.Monster, "Slime", new TileCoord(0, 0), Direction8.S, StepCooldownMs: 140));
        client.HandleMessageForTests(new EntitySpawnMessage(2, Guid.NewGuid(), EntityKind.Player, "Bob", new TileCoord(0, 0), Direction8.S, StepCooldownMs: 140));

        // Both confirm a step to tile X=1 at t=0.
        client.HandleMessageForTests(Snapshot(2, isComplete: true,
            new EntityStateSnapshot(1, new TileCoord(1, 0), Direction8.E),
            new EntityStateSnapshot(2, new TileCoord(1, 0), Direction8.E)));

        // Sample well past the hop duration (160ms) so the monster has settled, but the player is still inside its
        // ~75ms buffer lag relative to its confirmed tile. The monster sits on tile 1; the player has NOT (yet) reached it.
        var renders = client.GetRenderStates(TimeSpan.FromMilliseconds(300)).ToDictionary(r => r.NetworkId);

        var monster = renders[1];
        var player = renders[2];

        // The monster renders EXACTLY on its authoritative tile (== AuthoritativeTile == confirmed tile 1).
        Assert.Equal(EntityKind.Monster, monster.Kind);
        Assert.Equal(new TileCoord(1, 0), monster.AuthoritativeTile);
        Assert.Equal(1, monster.Position.X, 6);
        Assert.Equal(0, monster.Position.Y, 6);
        // At rest its hop arc is back on the ground.
        Assert.Equal(0d, monster.HopHeight, 6);

        // The player ALSO confirmed tile 1, but renders through the buffered interpolator — at 300ms its render has
        // caught up by now, so to make the contrast robust we sample EARLY (within the buffer) instead.
        Assert.Equal(EntityKind.Player, player.Kind);
        Assert.Equal(new TileCoord(1, 0), player.AuthoritativeTile);
        Assert.Equal(0d, player.HopHeight, 6); // players never carry a hop arc
    }

    // The contrast at an EARLY sample: right after the confirm, the buffered Player render still sits at its origin
    // (inside the playout buffer) while the Monster has ALREADY begun hopping toward the new tile (no buffer delay).
    [Fact]
    public void MonsterHasNoPlayoutBufferUnlikePlayer()
    {
        using var client = CreateClient(out _);
        client.HandleMessageForTests(new ServerHelloMessage("test", ProtocolCodec.Version, 20, 140, 30));
        client.HandleMessageForTests(new EntitySpawnMessage(1, Guid.NewGuid(), EntityKind.Monster, "Slime", new TileCoord(0, 0), Direction8.S, StepCooldownMs: 140));
        client.HandleMessageForTests(new EntitySpawnMessage(2, Guid.NewGuid(), EntityKind.Player, "Bob", new TileCoord(0, 0), Direction8.S, StepCooldownMs: 140));

        client.HandleMessageForTests(Snapshot(2, isComplete: true,
            new EntityStateSnapshot(1, new TileCoord(1, 0), Direction8.E),
            new EntityStateSnapshot(2, new TileCoord(1, 0), Direction8.E)));

        // Sample at 40ms — inside the player's ~75ms remote buffer (so the player is still pinned at origin), but the
        // monster's hop (no buffer) has already advanced part-way toward tile 1.
        var renders = client.GetRenderStates(TimeSpan.FromMilliseconds(40)).ToDictionary(r => r.NetworkId);

        Assert.True(renders[1].Position.X > 0.05, $"monster should have started hopping immediately (X={renders[1].Position.X})");
        Assert.Equal(0d, renders[2].Position.X, 6); // player still buffered at origin
    }

    // A Monster placeholder revealed by a later EntitySpawn (snapshot creates it as a Player placeholder first) STILL
    // routes to the hop path: it ends up resting on its latest confirmed tile, not buffered behind it.
    [Fact]
    public void PlaceholderRevealedAsMonsterRoutesToHopPath()
    {
        using var client = CreateClient(out _);
        client.HandleMessageForTests(new ServerHelloMessage("test", ProtocolCodec.Version, 20, 140, 30));

        // First sighting is a snapshot — created as a Player placeholder (#42).
        client.HandleMessageForTests(Snapshot(1, isComplete: true, new EntityStateSnapshot(42, new TileCoord(0, 0), Direction8.S)));
        // Then the real EntitySpawn reveals it is a Monster.
        client.HandleMessageForTests(new EntitySpawnMessage(42, Guid.NewGuid(), EntityKind.Monster, "Slime", new TileCoord(0, 0), Direction8.S, StepCooldownMs: 140));

        // A move confirm; after the hop settles, it rests on the latest tile (proving the hop driver attached).
        client.HandleMessageForTests(Snapshot(2, isComplete: true, new EntityStateSnapshot(42, new TileCoord(1, 0), Direction8.E)));
        var render = Assert.Single(client.GetRenderStates(TimeSpan.FromMilliseconds(300)));

        Assert.Equal(EntityKind.Monster, render.Kind);
        Assert.Equal(1, render.Position.X, 6);
        Assert.Equal(0, render.Position.Y, 6);
    }

    // A backlog of confirms (several tiles between two render samples) catches the Monster up to the NEWEST tile — no
    // accumulated lag through the live client path.
    [Fact]
    public void MonsterBacklogCatchesUpToNewestTile()
    {
        using var client = CreateClient(out _);
        client.HandleMessageForTests(new ServerHelloMessage("test", ProtocolCodec.Version, 20, 140, 30));
        client.HandleMessageForTests(new EntitySpawnMessage(1, Guid.NewGuid(), EntityKind.Monster, "Slime", new TileCoord(0, 0), Direction8.S, StepCooldownMs: 140));

        // A burst of confirms (a hitch) without sampling in between — tiles 1..4 all arrive.
        for (var x = 1; x <= 4; x++)
        {
            client.HandleMessageForTests(Snapshot((uint)(x + 1), isComplete: true, new EntityStateSnapshot(1, new TileCoord(x, 0), Direction8.E)));
        }

        // After the hop to the newest tile settles, the monster rests on tile 4 (not crawling through 1,2,3).
        var render = Assert.Single(client.GetRenderStates(TimeSpan.FromMilliseconds(400)));
        Assert.Equal(new TileCoord(4, 0), render.AuthoritativeTile);
        Assert.Equal(4, render.Position.X, 6);
    }

    // The live F1 knob (SetMonsterHopDurationMs) retunes a monster's hop with no restart.
    [Fact]
    public void LiveHopDurationKnobRetunesMonster()
    {
        using var client = CreateClient(out _);
        client.HandleMessageForTests(new ServerHelloMessage("test", ProtocolCodec.Version, 20, 140, 30));
        client.HandleMessageForTests(new EntitySpawnMessage(1, Guid.NewGuid(), EntityKind.Monster, "Slime", new TileCoord(0, 0), Direction8.S, StepCooldownMs: 140));

        Assert.Equal(MmoClient.DefaultMonsterHopDurationMs, client.MonsterHopDurationMs);
        client.SetMonsterHopDurationMs(80d);
        Assert.Equal(80d, client.MonsterHopDurationMs);

        // Confirm a step; with the 80ms hop it is fully settled by 120ms (still mid-hop at the 160ms default).
        client.HandleMessageForTests(Snapshot(2, isComplete: true, new EntityStateSnapshot(1, new TileCoord(1, 0), Direction8.E)));
        var render = Assert.Single(client.GetRenderStates(TimeSpan.FromMilliseconds(120)));
        Assert.Equal(1, render.Position.X, 6); // settled -> the live knob took effect
        Assert.Equal(0d, render.HopHeight, 6);
    }

    private static MmoClient CreateClient(out List<IProtocolMessage> outbound)
    {
        outbound = [];
        var captured = outbound;
        var client = new MmoClient(new ClientConnectionOptions("127.0.0.1", 1, "test", "account", "display"));
        client.OutboundSinkForTests = (message, _) => captured.Add(message);
        return client;
    }

    private static WorldSnapshotMessage Snapshot(uint sequence, bool isComplete, params EntityStateSnapshot[] entities)
    {
        return new WorldSnapshotMessage(10, sequence, entities.Length, isComplete, 0, 1, entities);
    }
}
