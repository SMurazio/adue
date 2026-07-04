using Mmo.Server.Configuration;
using Mmo.Server.Data;
using Mmo.Server.Runtime;
using Mmo.Shared.Domain;
using Xunit;

namespace Mmo.Server.Tests;

// ECOLOGY E2 (docs/ecology-v1-design.md §5.2 + the E2 task's additional acceptance list): "boot-wiring" coverage
// (no RunAsync, no live tick thread — mirrors AuthoredWorldTests/ZoneForTests) for RegionSpawner materialization,
// the kill hook, the no-spawn-near-player rule, D7's overgrown spawn modifiers, and /clearspawners' region-
// ecology cleanup (D10). Every assertion drives GameServer's real production methods through the ECOLOGY E2 test
// seams (EcologyForTests / RegionSpawnersForTests / MaterializeRegionSpawnersForTests / KillMonsterForTests /
// ClearRegionSpawnerMonstersForTests) — the SAME methods TickCore / KillMonster / HandleClearSpawnersCommand call
// in production, just made tick-count-parameterized so this stays deterministic with no real-time wait and no
// data race against a live tick thread.
public sealed class RegionSpawnerIntegrationTests
{
    private const int TickRate = 20;

    // The pacing gate GameServer itself derives (`2 * options.TickRate`) — mirrored here so a test can drive
    // exactly one pacing window per MaterializeRegionSpawnersForTests call.
    private const uint PacingTicks = 2 * TickRate;

    // A 384x384 PROCEDURAL zone (GenVersion defaults to 1 — see ServerOptions.GenVersion) — big enough that all
    // three REAL starter regions (Content/ecology.json, loaded automatically by GameServer, propagates to this
    // test project's output dir per the csproj) fit entirely inside it. Every OTHER integration test file in this
    // suite uses a 64x64 test zone, which puts every starter region rect fully out of bounds — deliberately NOT
    // reused here, since this suite needs the regions to actually exist.
    private static GameServer CreateServer()
    {
        var options = new ServerOptions(
            Port: 0,
            TickRate: TickRate,
            ConnectionKey: "region-spawner-test",
            DatabaseProvider: DatabaseProvider.Sqlite,
            ConnectionString: "Data Source=:memory:",
            MigrationsPath: "unused",
            WorldWidthTiles: 384,
            WorldHeightTiles: 384,
            StepCooldownMs: 250,
            PersistenceCheckpointSeconds: 15,
            InterestRadius: 18f,
            MaxVisibleEntities: 150,
            SpawnDistribution: SpawnDistribution.Distributed,
            AdminNames: new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        return new GameServer(options, new NullCharacterRepository());
    }

    // Drives `windows` pacing windows starting at `startTick`, one MaterializeRegionSpawnersForTests call per
    // window — enough windows (12) comfortably fully populates the biggest starter maxLive (10, Slime Hollow).
    private static void Materialize(GameServer server, uint startTick = 0, int windows = 12)
    {
        for (var i = 0; i < windows; i++)
        {
            server.MaterializeRegionSpawnersForTests(startTick + ((uint)i * PacingTicks));
        }
    }

    [Fact]
    public void Boot_DerivesOneRegionSpawnerPerStarterRegionType_WithNonEmptySpawnTiles()
    {
        var server = CreateServer();
        var spawners = server.RegionSpawnersForTests;

        Assert.Equal(4, spawners.Count);
        Assert.Contains(spawners, s => s.RegionId == "slime_hollow" && s.TypeId == "slime");
        Assert.Contains(spawners, s => s.RegionId == "eastern_scrubland" && s.TypeId == "gnoll");
        Assert.Contains(spawners, s => s.RegionId == "the_verge" && s.TypeId == "slime");
        Assert.Contains(spawners, s => s.RegionId == "the_verge" && s.TypeId == "gnoll");
        Assert.All(spawners, s => Assert.NotEmpty(s.SpawnTiles));
    }

    [Fact]
    public void Materialization_ConvergesLiveCountToFloorOfStock_ForEveryStarterRegionType()
    {
        var server = CreateServer();
        Materialize(server);

        foreach (var spawner in server.RegionSpawnersForTests)
        {
            var stock = server.EcologyForTests.StockOf(spawner.RegionId, spawner.TypeId);
            var target = Math.Min((int)Math.Floor(stock), spawner.BaseMaxLive);
            // Test assumption made explicit (fails loudly, not silently, if it ever doesn't hold): the derived
            // tile pool must be big enough to host the target without the round-robin cursor reusing a
            // still-occupied tile — true by construction (SpawnTileCountFor's "+slack" sizing) for every
            // starter region's un-overgrown target.
            Assert.True(
                spawner.SpawnTiles.Count >= target,
                $"{spawner.RegionId}/{spawner.TypeId}: only {spawner.SpawnTiles.Count} derived tiles for a target of {target}.");
            Assert.Equal(target, spawner.LiveCount);
        }
    }

    [Fact]
    public void Materialization_NeverSpawnsWithinSixUnitsOfAPlayer()
    {
        var server = CreateServer();
        var spawner = server.RegionSpawnersForTests.First(s => s.RegionId == "slime_hollow" && s.TypeId == "slime");

        // Park a player entity directly ON the FIRST derived spawn tile — the round-robin cursor tries it FIRST,
        // so if the exclusion rule works this exact tile is skipped for the whole run.
        var guardedTile = spawner.SpawnTiles[0];
        server.ZoneForTests.World.AddPlayer(
            networkId: 999,
            characterId: Guid.NewGuid(),
            displayName: "Guard",
            tile: guardedTile,
            ownerSession: null!,
            inventory: new Inventory(ItemRegistry.Default));

        Materialize(server);

        Assert.DoesNotContain(
            server.ZoneForTests.World.Entities,
            e => e.Kind == EntityKind.Monster && e.TileCoord == guardedTile);
        // The skip doesn't stall the whole region — the round-robin cursor moves on to other tiles.
        Assert.True(spawner.LiveCount > 0);
    }

    [Fact]
    public void KillMonster_DropsStockByExactlyN_ThroughTheRealKillMonsterPath()
    {
        var server = CreateServer();
        Materialize(server);

        var spawner = server.RegionSpawnersForTests.First(s => s.RegionId == "slime_hollow" && s.TypeId == "slime");
        var stockBefore = server.EcologyForTests.StockOf("slime_hollow", "slime");
        Assert.True(spawner.LiveCount >= 3, "test needs at least 3 live monsters to kill.");

        const int killCount = 3;
        var killedIds = spawner.LiveMonsterIds.Take(killCount).ToArray();
        foreach (var monsterId in killedIds)
        {
            Assert.True(server.ZoneForTests.World.TryGet(monsterId, out var monster));
            server.KillMonsterForTests(monster);
        }

        var stockAfter = server.EcologyForTests.StockOf("slime_hollow", "slime");
        Assert.Equal(stockBefore - killCount, stockAfter, 9);
        Assert.Equal(spawner.LiveCount, spawner.LiveMonsterIds.Count);
        Assert.DoesNotContain(killedIds, id => spawner.LiveMonsterIds.Contains(id));
    }

    [Fact]
    public void OvergrownState_AppliesStatModifiersOnlyToNewSpawns_NotRetroactively()
    {
        var server = CreateServer();
        Materialize(server);

        var spawner = server.RegionSpawnersForTests.First(s => s.RegionId == "slime_hollow" && s.TypeId == "slime");
        var type = spawner.Type;
        var originalLiveIds = spawner.LiveMonsterIds.ToArray();
        Assert.True(originalLiveIds.Length >= 2, "test needs at least 2 pre-existing live monsters.");

        // One monster stays alive for the whole test (the "never retroactively resized" control); one is killed
        // to open a slot for a fresh spawn once the region flips Overgrown.
        var survivorId = originalLiveIds[0];
        Assert.True(server.ZoneForTests.World.TryGet(survivorId, out var survivor));
        Assert.True(server.ZoneForTests.World.TryGet(originalLiveIds[1], out var killTarget));
        server.KillMonsterForTests(killTarget);

        // Force Overgrown (K=10 -> ratio 1.3 >= the D5 1.25 Overgrown threshold).
        Assert.True(server.EcologyForTests.TrySetStock("slime_hollow", "slime", 13d));
        Assert.Equal(EcologyState.PopulationState.Overgrown, server.EcologyForTests.StateOf("slime_hollow", "slime"));

        Materialize(server, startTick: PacingTicks * 20, windows: 20);

        var newMonsterId = spawner.LiveMonsterIds.Except(originalLiveIds).First();
        Assert.True(server.ZoneForTests.World.TryGet(newMonsterId, out var newMonster));

        var expectedMaxHealth = (int)Math.Ceiling(type.MaxHealth * 1.25d);
        Assert.Equal(expectedMaxHealth, newMonster.Stats.MaxHealth);
        Assert.NotNull(newMonster.RenderScaleOverride);
        Assert.Equal(type.RenderScale * 1.25d, newMonster.RenderScaleOverride!.Value);

        // The pre-existing survivor is completely untouched by its region flipping Overgrown.
        Assert.Equal(type.MaxHealth, survivor.Stats.MaxHealth);
        Assert.Null(survivor.RenderScaleOverride);
    }

    [Fact]
    public void ClearRegionSpawnerMonsters_DespawnsEveryLiveMonster_AndLeaksNothing()
    {
        var server = CreateServer();
        Materialize(server);

        var totalLiveBefore = server.RegionSpawnersForTests.Sum(s => s.LiveCount);
        Assert.True(totalLiveBefore > 0);
        var liveMonsterIdsBefore = server.RegionSpawnersForTests.SelectMany(s => s.LiveMonsterIds).ToArray();

        var despawnedCount = server.ClearRegionSpawnerMonstersForTests();

        Assert.Equal(totalLiveBefore, despawnedCount);
        Assert.All(server.RegionSpawnersForTests, s => Assert.Equal(0, s.LiveCount));
        Assert.Equal(0, server.RegionSpawnerOfMonsterCountForTests);
        foreach (var monsterId in liveMonsterIdsBefore)
        {
            Assert.False(server.ZoneForTests.World.TryGet(monsterId, out _));
        }

        // Ecology stock is UNTOUCHED by an admin clear (not a kill) — materialization refills from scratch on the
        // very next pacing window, exactly as if the world had just booted.
        Materialize(server, startTick: PacingTicks * 100);
        Assert.True(server.RegionSpawnersForTests.Sum(s => s.LiveCount) > 0);
    }

    // The boot-wiring test never touches persistence: GameServer's ctor only wires the repository into the
    // write-behind worker, which stays idle without sessions (mirrors AuthoredWorldTests' NullCharacterRepository).
    private sealed class NullCharacterRepository : ICharacterRepository
    {
        public Task<CharacterRecord> LoadOrCreateAsync(string accountName, string displayName, CancellationToken cancellationToken)
            => throw new NotSupportedException("Boot-wiring test: no logins expected.");

        public Task SavePositionAsync(Guid characterId, WorldVector position, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task<IReadOnlyList<ItemStack>> LoadItemsAsync(Guid characterId, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ItemStack>>([]);

        public Task SaveItemsAsync(Guid characterId, IReadOnlyList<ItemStack> changes, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}
