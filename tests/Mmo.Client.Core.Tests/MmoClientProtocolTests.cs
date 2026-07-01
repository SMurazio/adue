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
        // CONTINUOUS MIGRATION (v36): render is RAW (no buffering), so the cadence refresh is no longer observable via
        // a delayed render position. The interpolator cadence is still COMPUTED on confirm and surfaced via the
        // MovementDebug.EffectiveCadenceMs read-out: a late ServerHello (600ms cooldown ⇒ 600ms cadence) must retune
        // the early snapshot-created entity, so the confirmed-tile cadence reads 600ms (not the ~150ms default).
        client.HandleMessageForTests(new ServerHelloMessage("test", ProtocolCodec.Version, 20, 600, 30, 0.5f));
        client.HandleMessageForTests(Snapshot(2, isComplete: false, State(7, 1, 0)));

        Assert.Equal(600d, client.MovementDebug.EffectiveCadenceMs);
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

        client.HandleMessageForTests(new ServerHelloMessage("test", ProtocolCodec.Version, 20, 140, 30, 0.5f));
        client.HandleMessageForTests(new LoginResultMessage(true, characterId, "Local", ClientRole.Player, new TileCoord(5, 5), ""));
        client.HandleMessageForTests(new EntitySpawnMessage(9, characterId, EntityKind.Player, "Local", new TileCoord(5, 5), Direction8.S, StepCooldownMs: 140));

        // CONTINUOUS MIGRATION (Phase 4): PredictAndSendMove ships ONE per-input continuous MoveIntentMessage (raw
        // dir + dt). The returned seq matches the message's InputSeq; the snapshot still drives the tile_confirmed
        // trace. (No Zone here, so no predictor attaches — the seq comes from the pre-spawn fallback counter.)
        var sequence = client.PredictAndSendMove(1f, 0f, 0.05f);
        client.HandleMessageForTests(Snapshot(3, isComplete: true, new EntityStateSnapshot(9, WorldVector.FromTile(6, 5), Direction8.E)));

        Assert.Contains(outbound.OfType<MoveIntentMessage>(), move => move.InputSeq == sequence && move.DirX == 1f && move.DirY == 0f);
        Assert.Equal(9u, client.MovementDebug.LastConfirmedNetworkId);
        Assert.Equal(new TileCoord(6, 5), client.MovementDebug.LastConfirmedTile);
        Assert.Equal(3u, client.MovementDebug.LastConfirmedSnapshotSequence);
        Assert.Contains(lines, line => line.Contains("event=tile_confirmed", StringComparison.Ordinal));
    }

    [Fact]
    public void MovementDebugTraceIsSilentWhenDisabled()
    {
        var lines = new List<string>();
        using var client = CreateClient(out _, debugMovement: false, lines.Add);

        client.PredictAndSendMove(1f, 0f, 0.05f);
        client.RecordFrameHitch(40, 1, 0, 0);

        Assert.False(client.DebugMovementEnabled);
        // Console trace stays silent when disabled (no spam, no I/O). CONTINUOUS MIGRATION (v36): the per-move
        // trace recording rode the deleted MoveInput machinery, so LastSent* are no longer asserted here.
        Assert.Empty(lines);
    }

    [Fact]
    public void MovementDebugTraceRecordsFrameHitchContextWhenEnabled()
    {
        var lines = new List<string>();
        using var client = CreateClient(out _, debugMovement: true, lines.Add);
        var characterId = Guid.NewGuid();

        client.HandleMessageForTests(new ServerHelloMessage("test", ProtocolCodec.Version, 20, 140, 30, 0.5f));
        client.HandleMessageForTests(new LoginResultMessage(true, characterId, "Local", ClientRole.Player, new TileCoord(5, 5), ""));
        client.HandleMessageForTests(new EntitySpawnMessage(9, characterId, EntityKind.Player, "Local", new TileCoord(5, 5), Direction8.S, StepCooldownMs: 140));
        client.HandleMessageForTests(Snapshot(3, isComplete: true, new EntityStateSnapshot(9, WorldVector.FromTile(6, 5), Direction8.E)));

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
        client.HandleMessageForTests(new ServerHelloMessage("test", ProtocolCodec.Version, 20, 140, 30, 0.5f));
        client.HandleMessageForTests(new LoginResultMessage(true, characterId, "Local", ClientRole.Player, new TileCoord(5, 5), ""));
        client.HandleMessageForTests(new EntitySpawnMessage(9, characterId, EntityKind.Player, "Local", new TileCoord(5, 5), Direction8.S, StepCooldownMs: 70));

        client.HandleMessageForTests(Snapshot(3, isComplete: true, new EntityStateSnapshot(9, WorldVector.FromTile(6, 5), Direction8.S)));
        Assert.Equal(100d, client.MovementDebug.EffectiveCadenceMs);

        // A MovementSpeedChanged retunes the entity's cadence (back toward the slower 150ms here).
        client.HandleMessageForTests(new MovementSpeedChangedMessage(9, 150));
        client.HandleMessageForTests(Snapshot(4, isComplete: true, new EntityStateSnapshot(9, WorldVector.FromTile(7, 5), Direction8.S)));
        Assert.Equal(150d, client.MovementDebug.EffectiveCadenceMs);
    }

    [Fact]
    public void EntityWithoutPerEntityCadenceFallsBackToServerHelloGlobal()
    {
        using var client = CreateClient(out _);
        var characterId = Guid.NewGuid();

        client.HandleMessageForTests(new ServerHelloMessage("test", ProtocolCodec.Version, 20, 200, 30, 0.5f));
        client.HandleMessageForTests(new LoginResultMessage(true, characterId, "Local", ClientRole.Player, new TileCoord(5, 5), ""));

        // A snapshot-created placeholder (no EntitySpawn yet) carries no per-entity cooldown, so it tweens
        // at the ServerHello global (200ms ⇒ 200ms quantised).
        client.HandleMessageForTests(Snapshot(3, isComplete: true, new EntityStateSnapshot(42, WorldVector.FromTile(5, 5), Direction8.S)));
        client.HandleMessageForTests(Snapshot(4, isComplete: true, new EntityStateSnapshot(42, WorldVector.FromTile(6, 5), Direction8.S)));
        Assert.Equal(200d, client.MovementDebug.EffectiveCadenceMs);
    }

    // PLAYER↔PLAYER COLLISION: the LOCAL player's predicted obstacle gather now keeps MONSTERS *and* OTHER PLAYERS and
    // EXCLUDES the local player itself (it must NEVER be its own obstacle — that would pin it in place), emitted in stable
    // NetworkId order (parity with the server). Mutual prediction (two predicted bodies) is FEEL — the human tests two
    // clients; this pins the gather composition + self-exclusion + Id-sort headlessly.
    [Fact]
    public void EntityObstacleGatherExcludesSelf_IncludesOtherPlayersAndMonsters_IdSorted()
    {
        using var client = CreateClient(out _);
        var localCharacter = Guid.NewGuid();

        // BodyRadius 0.5 ⇒ gather reach = 2*0.5 + 2.0 = 3.0 units around the local centre (1 tile == 1 world unit).
        client.HandleMessageForTests(new ServerHelloMessage("test", ProtocolCodec.Version, 20, 140, 30, 0.5f));
        client.HandleMessageForTests(new LoginResultMessage(true, localCharacter, "Local", ClientRole.Player, new TileCoord(5, 5), ""));
        client.HandleMessageForTests(new EntitySpawnMessage(9, localCharacter, EntityKind.Player, "Local", new TileCoord(5, 5), Direction8.S, StepCooldownMs: 140));
        Assert.Equal(9u, client.LocalNetworkId);

        // Three OTHER bodies within reach (distinct characterIds ⇒ none is the local player): two players + a monster.
        client.HandleMessageForTests(new EntitySpawnMessage(20, Guid.NewGuid(), EntityKind.Player, "P20", new TileCoord(6, 5), Direction8.S, StepCooldownMs: 140));
        client.HandleMessageForTests(new EntitySpawnMessage(10, Guid.NewGuid(), EntityKind.Player, "P10", new TileCoord(7, 5), Direction8.S, StepCooldownMs: 140));
        client.HandleMessageForTests(new EntitySpawnMessage(15, Guid.NewGuid(), EntityKind.Monster, "M15", new TileCoord(5, 6), Direction8.S, StepCooldownMs: 140));
        // A body FAR outside the gather box ⇒ dropped by the distance cutoff.
        client.HandleMessageForTests(new EntitySpawnMessage(30, Guid.NewGuid(), EntityKind.Player, "Far", new TileCoord(20, 20), Direction8.S, StepCooldownMs: 140));

        var obstacles = client.GatherEntityObstaclesForTests(5d, 5d);

        // SELF never an obstacle (no circle at the local centre); the far body is out of reach.
        Assert.DoesNotContain(obstacles, c => c.X == 5d && c.Y == 5d);
        Assert.DoesNotContain(obstacles, c => c.X == 20d && c.Y == 20d);
        // Exactly the three nearby bodies (players 10,20 + monster 15), in STABLE Id order 10 → 15 → 20.
        Assert.Equal(3, obstacles.Count);
        Assert.Equal(7d, obstacles[0].X); Assert.Equal(5d, obstacles[0].Y); // id 10 (player)
        Assert.Equal(5d, obstacles[1].X); Assert.Equal(6d, obstacles[1].Y); // id 15 (monster)
        Assert.Equal(6d, obstacles[2].X); Assert.Equal(5d, obstacles[2].Y); // id 20 (player)
        Assert.All(obstacles, c => Assert.Equal(0.5d, c.Radius));
    }

    // PLAYER-COLLISION-TOGGLE: the client gather gates OTHER PLAYERS on the replicated flag (PlayerCollisionSettingMessage)
    // exactly as the server integrator does (parity). Flag OFF ⇒ other players are excluded, monsters still included; flag
    // back ON ⇒ both included. Self is always excluded regardless.
    [Fact]
    public void EntityObstacleGather_GatesOtherPlayersOnTheReplicatedFlag_MonstersAlwaysIncluded()
    {
        using var client = CreateClient(out _);
        var localCharacter = Guid.NewGuid();

        client.HandleMessageForTests(new ServerHelloMessage("test", ProtocolCodec.Version, 20, 140, 30, 0.5f));
        client.HandleMessageForTests(new LoginResultMessage(true, localCharacter, "Local", ClientRole.Player, new TileCoord(5, 5), ""));
        client.HandleMessageForTests(new EntitySpawnMessage(9, localCharacter, EntityKind.Player, "Local", new TileCoord(5, 5), Direction8.S, StepCooldownMs: 140));
        client.HandleMessageForTests(new EntitySpawnMessage(10, Guid.NewGuid(), EntityKind.Player, "P10", new TileCoord(7, 5), Direction8.S, StepCooldownMs: 140));
        client.HandleMessageForTests(new EntitySpawnMessage(15, Guid.NewGuid(), EntityKind.Monster, "M15", new TileCoord(5, 6), Direction8.S, StepCooldownMs: 140));

        // Default (no setting received) is ON — both the player and the monster are obstacles.
        Assert.True(client.PlayerCollisionEnabled);
        Assert.Equal(2, client.GatherEntityObstaclesForTests(5d, 5d).Count);

        // Flag OFF ⇒ the other player is dropped; the monster remains (at (5,6)). Self never appears.
        client.HandleMessageForTests(new PlayerCollisionSettingMessage(false));
        Assert.False(client.PlayerCollisionEnabled);
        var off = client.GatherEntityObstaclesForTests(5d, 5d);
        Assert.Single(off);
        Assert.Equal(5d, off[0].X); Assert.Equal(6d, off[0].Y); // the monster
        Assert.DoesNotContain(off, c => c.X == 7d && c.Y == 5d); // the player is gone
        Assert.DoesNotContain(off, c => c.X == 5d && c.Y == 5d); // self never an obstacle

        // Flag back ON ⇒ both bodies included again.
        client.HandleMessageForTests(new PlayerCollisionSettingMessage(true));
        Assert.True(client.PlayerCollisionEnabled);
        Assert.Equal(2, client.GatherEntityObstaclesForTests(5d, 5d).Count);
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
            new EntityStateSnapshot(1, WorldVector.FromTile(5, 5), Direction8.S, Depleted: false, Health: 70, MaxHealth: 100),
            new EntityStateSnapshot(2, WorldVector.FromTile(6, 6), Direction8.S)));

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
            new EntityStateSnapshot(9, WorldVector.FromTile(5, 5), Direction8.S, Depleted: false, Health: 100, MaxHealth: 100)));
        Assert.Equal(100, client.LocalStats!.Value.Health);

        // A monster hits the player: ONLY the snapshot HP drops (no new PlayerStatsMessage). The HUD's current HP
        // (LocalStats.Health) must follow it down.
        client.HandleMessageForTests(Snapshot(3, isComplete: true,
            new EntityStateSnapshot(9, WorldVector.FromTile(5, 5), Direction8.S, Depleted: false, Health: 73, MaxHealth: 100)));

        Assert.Equal(73, client.LocalStats!.Value.Health);
        // Max + mana + stamina preserved from PlayerStatsMessage.
        Assert.Equal(100, client.LocalStats!.Value.MaxHealth);
        Assert.Equal(60, client.LocalStats!.Value.Mana);
        Assert.Equal(30, client.LocalStats!.Value.Stamina);
    }

    // LIVING-ENEMIES P2-POLISH: the per-monster-TYPE tuning mirror + the monster home (red-tile anchor) tracking.
    [Fact]
    public void MonsterTuningAndSpawnerMarkerAreMirrored()
    {
        using var client = CreateClient(out _);

        Assert.Null(client.MonsterTuning);
        client.HandleMessageForTests(new MonsterTuningMessage(new MonsterTuningSnapshot(new[]
        {
            new MonsterTypeSnapshot("slime", "Slime", new[]
            {
                new MonsterTuningField("maxHealth", "hp (max)", 100, 1, 100000, true),
                new MonsterTuningField("respawnMs", "respawn (ms)", 5000, 0, 300000, true),
            }),
        })));
        Assert.NotNull(client.MonsterTuning);
        Assert.Equal("slime", client.MonsterTuning!.Value.Types[0].Id);
        Assert.Equal("respawnMs", client.MonsterTuning!.Value.Types[0].Fields[1].Key);
        Assert.Equal(5000, client.MonsterTuning!.Value.Types[0].Fields[1].Value, 6);
        Assert.Equal(1, client.MonsterTuningVersion);

        // LIVING-ENEMIES P3: a spawner marker keyed by spawner id is added on Active=true.
        client.HandleMessageForTests(new SpawnerMarkerMessage(7, new TileCoord(8, 9), true));
        Assert.True(client.SpawnerMarkers.TryGetValue(7, out var tile));
        Assert.Equal(new TileCoord(8, 9), tile);

        // A monster ENTITY despawn (its network id, NOT the spawner id) must NOT drop the persistent marker.
        client.HandleMessageForTests(new EntityDespawnMessage(5, 42));
        Assert.True(client.SpawnerMarkers.ContainsKey(7));

        // The marker is dropped only by an explicit Active=false (the spawner left AOI / was removed).
        client.HandleMessageForTests(new SpawnerMarkerMessage(7, default, false));
        Assert.False(client.SpawnerMarkers.ContainsKey(7));
    }

    private static WorldSnapshotMessage Snapshot(uint sequence, bool isComplete, params EntityStateSnapshot[] entities)
    {
        return new WorldSnapshotMessage(10, sequence, entities.Length, isComplete, 0, 1, entities);
    }

    private static EntityStateSnapshot State(uint networkId, int x, int y)
    {
        return new EntityStateSnapshot(networkId, WorldVector.FromTile(x, y), Direction8.S);
    }
}
