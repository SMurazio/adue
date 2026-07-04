using System.Linq;
using Mmo.Server.Configuration;
using Mmo.Server.Data;
using Mmo.Server.Runtime;
using Mmo.Shared.Domain;
using Xunit;

namespace Mmo.Server.Tests;

// ECOLOGY E3 (docs/ecology-v1-design.md D8, §5.3, §8 E3): headless "boot-wiring" coverage (no RunAsync, no live
// tick thread — mirrors EcologyWireTests/RegionSpawnerIntegrationTests) for the GameServer load/save wiring:
// restart-survival bit-identity, missing-row K-seed fallback, orphan-row ignoring, and clamp/reject-on-load.
// Every test drives the REAL production seams (EcologyForTests, SaveEcologyPopulationsForTests, GameServer's
// ctor-time load) against a REAL Sqlite database (TestSqliteDatabase), not a fake — this is the first world-state
// persistence path, so the repository itself must be exercised, not stubbed out.
public sealed class EcologyPersistenceIntegrationTests
{
    private static GameServer CreateServer(IEcologyRepository ecologyRepository)
    {
        var options = new ServerOptions(
            Port: 0,
            TickRate: 20,
            ConnectionKey: "ecology-persistence-test",
            DatabaseProvider: DatabaseProvider.Sqlite,
            ConnectionString: "Data Source=:memory:",
            MigrationsPath: "unused",
            WorldWidthTiles: 64,
            WorldHeightTiles: 64,
            StepCooldownMs: 250,
            PersistenceCheckpointSeconds: 15,
            InterestRadius: 18f,
            MaxVisibleEntities: 150,
            SpawnDistribution: SpawnDistribution.Distributed,
            AdminNames: new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        return new GameServer(options, new NullCharacterRepository(), ecologyRepository);
    }

    [Fact]
    public async Task SaveThenBootANewServer_RestoresStockAndPressureBitIdentically()
    {
        using var database = await TestSqliteDatabase.CreateMigratedAsync();
        var repository = new SqliteEcologyRepository(database.ConnectionString);

        var serverA = CreateServer(repository);
        var ecologyA = serverA.EcologyForTests;

        // Drive the math off the K-seed so every persisted value is non-trivial (not just "still at K").
        ecologyA.EcologyTick();
        ecologyA.RecordKill("slime_hollow", "slime");
        ecologyA.RecordKill("the_verge", "gnoll");
        ecologyA.EcologyTick();
        ecologyA.EcologyTick();

        var expected = ecologyA.SnapshotAll();
        Assert.NotEmpty(expected);

        await serverA.SaveEcologyPopulationsForTests();

        var serverB = CreateServer(repository);
        var ecologyB = serverB.EcologyForTests;
        var actual = ecologyB.SnapshotAll().ToDictionary(r => (r.RegionId, r.TypeId));

        foreach (var row in expected)
        {
            Assert.True(actual.TryGetValue((row.RegionId, row.TypeId), out var restored), $"Missing restored row for {row.RegionId}/{row.TypeId}.");
            Assert.Equal(row.Stock, restored.Stock); // exact — no restart-heals-the-world tolerance
            Assert.Equal(row.Pressure, restored.Pressure);
        }
    }

    [Fact]
    public async Task Boot_MissingRowForARegionType_LeavesItAtItsKSeed()
    {
        using var database = await TestSqliteDatabase.CreateMigratedAsync();
        var repository = new SqliteEcologyRepository(database.ConnectionString);

        // Persist ONLY slime_hollow/slime — eastern_scrubland/gnoll and the_verge's two types have no saved row.
        await repository.SaveAllAsync(
            [new RegionPopulationRecord("slime_hollow", "slime", 3d, 2d, 999)],
            CancellationToken.None);

        var server = CreateServer(repository);
        var ecology = server.EcologyForTests;

        Assert.Equal(3d, ecology.StockOf("slime_hollow", "slime"));
        Assert.Equal(2d, ecology.PressureOf("slime_hollow", "slime"));

        // No row saved for these -> still at their authored K (D1: "S seeds at K"), zero pressure.
        Assert.Equal(8d, ecology.StockOf("eastern_scrubland", "gnoll"));
        Assert.Equal(0d, ecology.PressureOf("eastern_scrubland", "gnoll"));
        Assert.Equal(6d, ecology.StockOf("the_verge", "slime"));
        Assert.Equal(6d, ecology.StockOf("the_verge", "gnoll"));
    }

    [Fact]
    public async Task Boot_OrphanedRowForAnUnknownRegionOrType_IsIgnoredWithoutAffectingKnownRows()
    {
        using var database = await TestSqliteDatabase.CreateMigratedAsync();
        var repository = new SqliteEcologyRepository(database.ConnectionString);

        await repository.SaveAllAsync(
            [
                new RegionPopulationRecord("ghost_region", "ghost_type", 42d, 42d, 1),
                new RegionPopulationRecord("slime_hollow", "ghost_type", 42d, 42d, 1),
                new RegionPopulationRecord("slime_hollow", "slime", 5d, 1d, 1),
            ],
            CancellationToken.None);

        var server = CreateServer(repository);
        var ecology = server.EcologyForTests;

        // The real row still applies; the two orphans (unknown region, and a known region/unknown type) never
        // throw and never leak into any other region×type's state.
        Assert.Equal(5d, ecology.StockOf("slime_hollow", "slime"));
        Assert.Equal(1d, ecology.PressureOf("slime_hollow", "slime"));
        Assert.Equal(8d, ecology.StockOf("eastern_scrubland", "gnoll"));
    }

    [Fact]
    public async Task Boot_LoadedStockAboveTheCurrentCap_ClampsTo1Point5K()
    {
        using var database = await TestSqliteDatabase.CreateMigratedAsync();
        var repository = new SqliteEcologyRepository(database.ConnectionString);

        // slime_hollow/slime is authored at K=10 -> Smax = 1.5*10 = 15. 999 is a manifest-changed-since-save case.
        await repository.SaveAllAsync(
            [new RegionPopulationRecord("slime_hollow", "slime", 999d, 5d, 1)],
            CancellationToken.None);

        var server = CreateServer(repository);
        var ecology = server.EcologyForTests;

        Assert.Equal(15d, ecology.StockOf("slime_hollow", "slime"));
        Assert.Equal(5d, ecology.PressureOf("slime_hollow", "slime"));
    }

    [Fact]
    public async Task Boot_LoadedStockBelowTheCurrentFloor_ClampsToSmin()
    {
        using var database = await TestSqliteDatabase.CreateMigratedAsync();
        var repository = new SqliteEcologyRepository(database.ConnectionString);

        // the_verge/slime is authored at K=6 -> Smin = max(0.05*6, 0.5) = 0.5. A negative persisted value only
        // happens if the row was hand-edited/corrupted; it must still clamp, never propagate a negative stock.
        await repository.SaveAllAsync(
            [new RegionPopulationRecord("the_verge", "slime", -3d, 0d, 1)],
            CancellationToken.None);

        var server = CreateServer(repository);
        var ecology = server.EcologyForTests;

        Assert.Equal(0.5d, ecology.StockOf("the_verge", "slime"));
    }

    // Corruption is injected via RAW SQL, not SaveAllAsync — the Microsoft.Data.Sqlite driver refuses to WRITE
    // NaN outright (the first gate run proved the writer path cannot even create such rows), and the 006 schema's
    // NOT NULL constraint rejects NULL at the DDL layer (the second gate run proved THAT), so the reachable
    // corrupt REAL states via SQL are the ±9e999 literals SQLite evaluates to ±Infinity — exercising the boot
    // loader's non-finite rejection. (The repository's IsDBNull row skip stays as defense-in-depth behind the
    // schema constraint.) Either way: the K seed must survive.
    [Theory]
    [InlineData("9e999")]
    [InlineData("-9e999")]
    public async Task Boot_CorruptLoadedStock_IsRejectedAndKeepsTheKSeed(string corruptStockSqlLiteral)
    {
        using var database = await TestSqliteDatabase.CreateMigratedAsync();
        var repository = new SqliteEcologyRepository(database.ConnectionString);

        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var insert = connection.CreateCommand();
            insert.CommandText =
                "insert into region_populations (region_id, type_id, stock, pressure, updated_at_tick) " +
                $"values ('slime_hollow', 'slime', {corruptStockSqlLiteral}, 5.0, 1);";
            await insert.ExecuteNonQueryAsync();
        }

        var server = CreateServer(repository);
        var ecology = server.EcologyForTests;

        // The whole row is rejected (stock AND pressure) rather than partially applied — an invalid stock value
        // means the row can't be trusted, so pressure stays at its seed (0) too, not the persisted 5.
        Assert.Equal(10d, ecology.StockOf("slime_hollow", "slime"));
        Assert.Equal(0d, ecology.PressureOf("slime_hollow", "slime"));
    }

    // E3 review L3: WHOLE-row rejection must be symmetric — a finite stock with a NON-FINITE PRESSURE previously
    // half-applied (stock landed, pressure silently kept its seed, no warning), contradicting the stated policy.
    // This pins: corrupt pressure -> NEITHER value applies, both keep their seeds.
    [Fact]
    public async Task Boot_CorruptLoadedPressure_RejectsTheWholeRow()
    {
        using var database = await TestSqliteDatabase.CreateMigratedAsync();
        var repository = new SqliteEcologyRepository(database.ConnectionString);

        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var insert = connection.CreateCommand();
            insert.CommandText =
                "insert into region_populations (region_id, type_id, stock, pressure, updated_at_tick) " +
                "values ('slime_hollow', 'slime', 5.0, 9e999, 1);";
            await insert.ExecuteNonQueryAsync();
        }

        var server = CreateServer(repository);
        var ecology = server.EcologyForTests;

        Assert.Equal(10d, ecology.StockOf("slime_hollow", "slime"));   // K seed, NOT the row's finite 5.0
        Assert.Equal(0d, ecology.PressureOf("slime_hollow", "slime")); // seed, NOT the corrupt value
    }

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
