using LiteNetLib;
using Mmo.Shared.Domain;
using Mmo.Shared.Protocol;
using Xunit;

namespace Mmo.Client.Core.Tests;

public sealed class MmoClientProtocolTests
{
    [Fact]
    public void ChunkedSnapshotAppliesAndAcksOnceAfterReassembly()
    {
        using var client = CreateClient(out var outbound);

        client.HandleMessageForTests(new WorldSnapshotMessage(
            10,
            7,
            2,
            true,
            0,
            2,
            [State(1, 10, 10)]));

        Assert.Empty(outbound);
        Assert.False(client.TryGetEntity(1, out _));

        client.HandleMessageForTests(new WorldSnapshotMessage(
            10,
            7,
            2,
            true,
            1,
            2,
            [State(2, 11, 10)]));

        var ack = Assert.Single(outbound.OfType<SnapshotAckMessage>());
        Assert.Equal(7u, ack.LastSnapshotSequence);
        Assert.True(client.TryGetEntity(1, out _));
        Assert.True(client.TryGetEntity(2, out _));
    }

    [Fact]
    public void PlayerStatsMessageUpdatesLocalStats()
    {
        using var client = CreateClient(out _);

        // COMBAT-S1: until the first PlayerStats arrives, the local vitals are unknown.
        Assert.Null(client.LocalStats);

        client.HandleMessageForTests(new PlayerStatsMessage(new CharacterStats(40, 100, 10, 120, 5, 80)));

        Assert.NotNull(client.LocalStats);
        Assert.Equal(new CharacterStats(40, 100, 10, 120, 5, 80), client.LocalStats!.Value);

        // A later replication (e.g. a dev-set confirm) overwrites it.
        client.HandleMessageForTests(new PlayerStatsMessage(new CharacterStats(100, 100, 120, 120, 80, 80)));
        Assert.Equal(100, client.LocalStats!.Value.Health);
        Assert.Equal(120, client.LocalStats!.Value.Mana);
    }

    [Fact]
    public void InvalidChunkAndStaleSnapshotAreDroppedWithoutAckOrStateChange()
    {
        using var client = CreateClient(out var outbound);

        client.HandleMessageForTests(new WorldSnapshotMessage(
            10,
            1,
            1,
            true,
            2,
            2,
            [State(1, 99, 99)]));

        Assert.Empty(outbound);
        Assert.False(client.TryGetEntity(1, out _));

        client.HandleMessageForTests(Snapshot(2, isComplete: true, State(1, 1, 1)));
        Assert.Single(outbound.OfType<SnapshotAckMessage>());
        Assert.True(client.TryGetEntity(1, out var applied));
        Assert.Equal(new TileCoord(1, 1), applied.Tile);

        client.HandleMessageForTests(Snapshot(1, isComplete: true, State(1, 5, 5)));

        Assert.Single(outbound.OfType<SnapshotAckMessage>());
        Assert.True(client.TryGetEntity(1, out var current));
        Assert.Equal(new TileCoord(1, 1), current.Tile);
    }

    [Fact]
    public void IncompleteSnapshotMergesAndFullSnapshotPrunesMissingEntities()
    {
        using var client = CreateClient(out _);

        client.HandleMessageForTests(Snapshot(1, isComplete: true, State(1, 1, 1), State(2, 2, 2)));
        client.HandleMessageForTests(Snapshot(2, isComplete: false, State(1, 3, 1)));

        Assert.True(client.TryGetEntity(1, out var moved));
        Assert.Equal(new TileCoord(3, 1), moved.Tile);
        Assert.True(client.TryGetEntity(2, out _));

        client.HandleMessageForTests(Snapshot(3, isComplete: true, State(1, 4, 1)));

        Assert.True(client.TryGetEntity(1, out var retained));
        Assert.Equal(new TileCoord(4, 1), retained.Tile);
        Assert.False(client.TryGetEntity(2, out _));
    }

    [Fact]
    public void PlaceholderFromSnapshotIsUpgradedByEntitySpawn()
    {
        using var client = CreateClient(out _);
        var characterId = Guid.NewGuid();

        client.HandleMessageForTests(Snapshot(1, isComplete: true, State(42, 8, 9)));
        Assert.True(client.TryGetEntity(42, out var placeholder));
        Assert.Equal("#42", placeholder.DisplayName);
        Assert.Equal(Guid.Empty, placeholder.CharacterId);

        client.HandleMessageForTests(new EntitySpawnMessage(
            42,
            characterId,
            EntityKind.Player,
            "RealName",
            new TileCoord(8, 9),
            Direction8.S,
            StepCooldownMs: 140));

        Assert.True(client.TryGetEntity(42, out var upgraded));
        Assert.Equal(characterId, upgraded.CharacterId);
        Assert.Equal("RealName", upgraded.DisplayName);
    }

    [Fact]
    public void PlaceholderAbsentFromIncompleteSnapshotsExpires()
    {
        using var client = CreateClient(out _);

        client.HandleMessageForTests(Snapshot(1, isComplete: false, State(42, 1, 1)));
        Assert.True(client.TryGetEntity(42, out _));

        for (var sequence = 2u; sequence <= 63u; sequence++)
        {
            client.HandleMessageForTests(Snapshot(sequence, isComplete: false, State(99, 2, 2)));
        }

        Assert.False(client.TryGetEntity(42, out _));
        Assert.True(client.TryGetEntity(99, out _));
    }

    [Fact]
    public void ServerHelloRefreshesInterpolatorCadenceForEntitiesCreatedEarly()
    {
        using var client = CreateClient(out _);

        client.HandleMessageForTests(Snapshot(1, isComplete: true, State(7, 0, 0)));
        client.HandleMessageForTests(new ServerHelloMessage("test", ProtocolCodec.Version, 20, 300, 30));
        client.HandleMessageForTests(Snapshot(2, isComplete: false, State(7, 1, 0)));

        var render = Assert.Single(client.GetRenderStates(TimeSpan.FromMilliseconds(200)));
        Assert.Equal(0, render.Position.X);
    }

    [Fact]
    public void EntityDespawnClearsLocalNetworkId()
    {
        using var client = CreateClient(out _);
        var characterId = Guid.NewGuid();

        client.HandleMessageForTests(new LoginResultMessage(true, characterId, "Local", ClientRole.Player, new TileCoord(3, 3), ""));
        client.HandleMessageForTests(new EntitySpawnMessage(9, characterId, EntityKind.Player, "Local", new TileCoord(3, 3), Direction8.S, StepCooldownMs: 140));

        Assert.Equal(9u, client.LocalNetworkId);
        Assert.True(client.TryGetEntity(9, out var local));
        Assert.True(local.IsLocal);

        client.HandleMessageForTests(new EntityDespawnMessage(3, 9));

        Assert.Null(client.LocalNetworkId);
        Assert.False(client.TryGetEntity(9, out _));
    }

    [Fact]
    public void MovementDebugTraceRecordsSentAndConfirmedTileWhenEnabled()
    {
        var lines = new List<string>();
        using var client = CreateClient(out var outbound, debugMovement: true, lines.Add);
        var characterId = Guid.NewGuid();

        client.HandleMessageForTests(new ServerHelloMessage("test", ProtocolCodec.Version, 20, 140, 30));
        client.HandleMessageForTests(new LoginResultMessage(true, characterId, "Local", ClientRole.Player, new TileCoord(5, 5), ""));
        client.HandleMessageForTests(new EntitySpawnMessage(9, characterId, EntityKind.Player, "Local", new TileCoord(5, 5), Direction8.S, StepCooldownMs: 140));

        var sequence = client.SendMoveIntent(true, Direction8.E);
        client.HandleMessageForTests(Snapshot(3, isComplete: true, new EntityStateSnapshot(9, new TileCoord(6, 5), Direction8.E)));

        // NET1 Stage 1: SendMoveIntent now ships the redundant-unreliable MoveInputMessage (full current state +
        // window), not the old reliable MoveIntentMessage. The head seq matches the returned sequence.
        Assert.Contains(outbound.OfType<MoveInputMessage>(), move => move.HeadSeq == sequence && move.Moving && move.Direction == Direction8.E);
        Assert.Equal(sequence, client.MovementDebug.LastSentSequence);
        Assert.Equal(Direction8.E, client.MovementDebug.LastSentDirection);
        Assert.Equal(9u, client.MovementDebug.LastConfirmedNetworkId);
        Assert.Equal(new TileCoord(6, 5), client.MovementDebug.LastConfirmedTile);
        Assert.Equal(3u, client.MovementDebug.LastConfirmedSnapshotSequence);
        Assert.True(client.MovementDebug.QueueDepth > 0);
        Assert.Equal(150d, client.MovementDebug.EffectiveCadenceMs);
        Assert.Contains(lines, line => line.Contains("event=move_intent", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("event=tile_confirmed", StringComparison.Ordinal));
    }

    [Fact]
    public void MovementDebugTraceIsSilentWhenDisabled()
    {
        var lines = new List<string>();
        using var client = CreateClient(out _, debugMovement: false, lines.Add);

        var sequence = client.SendMoveIntent(true, Direction8.E);
        client.RecordFrameHitch(40, 1, 0, 0);

        Assert.False(client.DebugMovementEnabled);
        // Console trace stays silent when disabled (no spam, no I/O).
        Assert.Empty(lines);
        // But the in-memory snapshot still tracks state so live debug HUDs (e.g. the Godot F3 panel)
        // can read interpolation/movement state without enabling the console trace.
        Assert.Equal(sequence, client.MovementDebug.LastSentSequence);
        Assert.Equal(Direction8.E, client.MovementDebug.LastSentDirection);
    }

    [Fact]
    public void MovementDebugTraceRecordsFrameHitchContextWhenEnabled()
    {
        var lines = new List<string>();
        using var client = CreateClient(out _, debugMovement: true, lines.Add);
        var characterId = Guid.NewGuid();

        client.HandleMessageForTests(new ServerHelloMessage("test", ProtocolCodec.Version, 20, 140, 30));
        client.HandleMessageForTests(new LoginResultMessage(true, characterId, "Local", ClientRole.Player, new TileCoord(5, 5), ""));
        client.HandleMessageForTests(new EntitySpawnMessage(9, characterId, EntityKind.Player, "Local", new TileCoord(5, 5), Direction8.S, StepCooldownMs: 140));
        client.HandleMessageForTests(Snapshot(3, isComplete: true, new EntityStateSnapshot(9, new TileCoord(6, 5), Direction8.E)));

        client.RecordFrameHitch(42.5, 1, 2, 3);

        var line = Assert.Single(lines, line => line.Contains("event=frame_hitch", StringComparison.Ordinal));
        Assert.Contains("durationMs=42.5", line);
        Assert.Contains("gc0=1", line);
        Assert.Contains("gc1=2", line);
        Assert.Contains("gc2=3", line);
        Assert.Contains("queueDepth=", line);
        Assert.Contains("cadenceMs=150", line);
        Assert.Contains("visible=1", line);
        Assert.Contains("state=LoggedIn", line);
    }

    [Fact]
    public void PerEntityCadenceFromSpawnOverridesTheServerHelloGlobal()
    {
        using var client = CreateClient(out _);
        var characterId = Guid.NewGuid();

        // Global cadence is 140ms (=150ms quantised), but this entity's spawn advertises a 70ms cooldown
        // (=100ms quantised), so its tween must use the per-entity cadence, not the global.
        client.HandleMessageForTests(new ServerHelloMessage("test", ProtocolCodec.Version, 20, 140, 30));
        client.HandleMessageForTests(new LoginResultMessage(true, characterId, "Local", ClientRole.Player, new TileCoord(5, 5), ""));
        client.HandleMessageForTests(new EntitySpawnMessage(9, characterId, EntityKind.Player, "Local", new TileCoord(5, 5), Direction8.S, StepCooldownMs: 70));

        client.HandleMessageForTests(Snapshot(3, isComplete: true, new EntityStateSnapshot(9, new TileCoord(6, 5), Direction8.S)));
        Assert.Equal(100d, client.MovementDebug.EffectiveCadenceMs);

        // A MovementSpeedChanged retunes the entity's cadence (back toward the slower 150ms here).
        client.HandleMessageForTests(new MovementSpeedChangedMessage(9, 150));
        client.HandleMessageForTests(Snapshot(4, isComplete: true, new EntityStateSnapshot(9, new TileCoord(7, 5), Direction8.S)));
        Assert.Equal(150d, client.MovementDebug.EffectiveCadenceMs);
    }

    [Fact]
    public void EntityWithoutPerEntityCadenceFallsBackToServerHelloGlobal()
    {
        using var client = CreateClient(out _);
        var characterId = Guid.NewGuid();

        client.HandleMessageForTests(new ServerHelloMessage("test", ProtocolCodec.Version, 20, 200, 30));
        client.HandleMessageForTests(new LoginResultMessage(true, characterId, "Local", ClientRole.Player, new TileCoord(5, 5), ""));

        // A snapshot-created placeholder (no EntitySpawn yet) carries no per-entity cooldown, so it tweens
        // at the ServerHello global (200ms ⇒ 200ms quantised).
        client.HandleMessageForTests(Snapshot(3, isComplete: true, new EntityStateSnapshot(42, new TileCoord(5, 5), Direction8.S)));
        client.HandleMessageForTests(Snapshot(4, isComplete: true, new EntityStateSnapshot(42, new TileCoord(6, 5), Direction8.S)));
        Assert.Equal(200d, client.MovementDebug.EffectiveCadenceMs);
    }

    private static MmoClient CreateClient(out List<IProtocolMessage> outbound, bool debugMovement = false, Action<string>? traceSink = null)
    {
        outbound = [];
        var captured = outbound;
        var client = new MmoClient(
            new ClientConnectionOptions("127.0.0.1", 1, "test", "account", "display"),
            new ClientMovementTrace(debugMovement, traceSink));
        client.OutboundSinkForTests = (message, _) => captured.Add(message);
        return client;
    }

    [Fact]
    public void SnapshotHealthThreadsToRenderState()
    {
        // COMBAT-S2A: the public HP on the snapshot threads through to the render state that drives the
        // overhead bar. A stat-bearing entity carries a partial HP (HasHealth=true, a fractional fill); a
        // stat-less entity carries 0/0 (HasHealth=false → the visual hides the bar).
        using var client = CreateClient(out _);

        client.HandleMessageForTests(Snapshot(
            1,
            isComplete: true,
            new EntityStateSnapshot(1, new TileCoord(5, 5), Direction8.S, Depleted: false, Health: 70, MaxHealth: 100),
            new EntityStateSnapshot(2, new TileCoord(6, 6), Direction8.S)));

        var renders = client.GetRenderStates(TimeSpan.FromMilliseconds(200))
            .ToDictionary(r => r.NetworkId);

        Assert.True(renders[1].HasHealth);
        Assert.Equal((ushort)70, renders[1].Health);
        Assert.Equal((ushort)100, renders[1].MaxHealth);
        Assert.Equal(0.70f, renders[1].HealthFraction, 3);

        Assert.False(renders[2].HasHealth);
        Assert.Equal(0f, renders[2].HealthFraction);
    }

    // LIVING-ENEMIES P2-POLISH item 1 (HUD HP fix): the local player's snapshot HP (the authoritative per-frame value
    // the overhead bar reads) syncs into LocalStats.Health so the HUD bar falls when the player takes damage. The
    // PlayerStatsMessage that seeds LocalStats is NOT re-sent on a hit; only the snapshot HP changes. MaxHealth + mana
    // + stamina stay from PlayerStatsMessage.
    [Fact]
    public void LocalSnapshotHealthSyncsIntoLocalStats()
    {
        using var client = CreateClient(out _);
        var characterId = Guid.NewGuid();

        client.HandleMessageForTests(new LoginResultMessage(true, characterId, "Local", ClientRole.Player, new TileCoord(5, 5), ""));
        client.HandleMessageForTests(new EntitySpawnMessage(9, characterId, EntityKind.Player, "Local", new TileCoord(5, 5), Direction8.S, StepCooldownMs: 140));
        // Login vitals (gives MaxHealth + mana + stamina). Full HP at first.
        client.HandleMessageForTests(new PlayerStatsMessage(new CharacterStats(100, 100, 60, 120, 30, 80)));
        // A snapshot carrying the local player at full HP (MaxHealth>0 so the sync is armed).
        client.HandleMessageForTests(Snapshot(2, isComplete: true,
            new EntityStateSnapshot(9, new TileCoord(5, 5), Direction8.S, Depleted: false, Health: 100, MaxHealth: 100)));
        Assert.Equal(100, client.LocalStats!.Value.Health);

        // A monster hits the player: ONLY the snapshot HP drops (no new PlayerStatsMessage). The HUD's current HP
        // (LocalStats.Health) must follow it down.
        client.HandleMessageForTests(Snapshot(3, isComplete: true,
            new EntityStateSnapshot(9, new TileCoord(5, 5), Direction8.S, Depleted: false, Health: 73, MaxHealth: 100)));

        Assert.Equal(73, client.LocalStats!.Value.Health);
        // Max + mana + stamina preserved from PlayerStatsMessage.
        Assert.Equal(100, client.LocalStats!.Value.MaxHealth);
        Assert.Equal(60, client.LocalStats!.Value.Mana);
        Assert.Equal(30, client.LocalStats!.Value.Stamina);
    }

    // LIVING-ENEMIES P2-POLISH: the per-monster-TYPE tuning mirror + the monster home (red-tile anchor) tracking.
    [Fact]
    public void MonsterTuningAndHomeAreMirrored()
    {
        using var client = CreateClient(out _);

        Assert.Null(client.MonsterTuning);
        client.HandleMessageForTests(new MonsterTuningMessage(new MonsterTuningSnapshot(new[]
        {
            new MonsterTypeSnapshot("slime", "Slime", 100, 0.8, 4, 2000, 5000, 6, 12, 1, 10, 1000),
        })));
        Assert.NotNull(client.MonsterTuning);
        Assert.Equal("slime", client.MonsterTuning!.Value.Types[0].Id);
        Assert.Equal(1, client.MonsterTuningVersion);

        client.HandleMessageForTests(new MonsterHomeMessage(42, new TileCoord(8, 9)));
        Assert.True(client.MonsterHomes.TryGetValue(42, out var home));
        Assert.Equal(new TileCoord(8, 9), home);

        // The home marker is dropped when the monster despawns.
        client.HandleMessageForTests(new EntityDespawnMessage(5, 42));
        Assert.False(client.MonsterHomes.ContainsKey(42));
    }

    private static WorldSnapshotMessage Snapshot(uint sequence, bool isComplete, params EntityStateSnapshot[] entities)
    {
        return new WorldSnapshotMessage(10, sequence, entities.Length, isComplete, 0, 1, entities);
    }

    private static EntityStateSnapshot State(uint networkId, int x, int y)
    {
        return new EntityStateSnapshot(networkId, new TileCoord(x, y), Direction8.S);
    }
}
