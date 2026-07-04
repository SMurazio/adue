using Microsoft.Data.Sqlite;
using Mmo.Server.Data;
using Xunit;

namespace Mmo.Server.Tests;

// ECOLOGY E3 (docs/ecology-v1-design.md D8, §5.3, §8 E3): repository-level coverage for SqliteEcologyRepository +
// migration 006 — mirrors SqliteCharacterItemsTests's pattern (TestSqliteDatabase.CreateMigratedAsync/CreateEmpty +
// SqliteMigrationRunner directly). GameServer-level restart/clamp/orphan acceptance lives in
// EcologyPersistenceIntegrationTests; this file only pins the repository + table in isolation.
public sealed class SqliteEcologyRepositoryTests
{
    [Fact]
    public async Task CleanBootstrapCreatesRegionPopulationsTable()
    {
        using var database = TestSqliteDatabase.CreateEmpty();
        var migrations = new SqliteMigrationRunner(database.ConnectionString, TestSqliteDatabase.MigrationsPath);

        await migrations.ApplyAsync(CancellationToken.None);

        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync();
        Assert.True(await TableExistsAsync(connection, "region_populations"));
    }

    [Fact]
    public async Task LoadAllReturnsEmptyOnAFreshlyMigratedDatabase()
    {
        using var database = await TestSqliteDatabase.CreateMigratedAsync();
        var repository = new SqliteEcologyRepository(database.ConnectionString);

        var rows = await repository.LoadAllAsync(CancellationToken.None);

        Assert.Empty(rows);
    }

    [Fact]
    public async Task SaveAllAndLoadAllRoundTripExactly()
    {
        using var database = await TestSqliteDatabase.CreateMigratedAsync();
        var repository = new SqliteEcologyRepository(database.ConnectionString);

        var saved = new List<RegionPopulationRecord>
        {
            new("slime_hollow", "slime", 7.5d, 3.25d, 4200),
            new("the_verge", "gnoll", 0.5d, 0d, 4200),
            // A value that is NOT tile-integer/round — pins float fidelity through the REAL column (SQLite's REAL
            // is an 8-byte IEEE754 double, the same representation as C# double, so this must be bit-identical).
            new("the_verge", "slime", 3.14159265358979d, 12.0000001d, 4200),
        };

        await repository.SaveAllAsync(saved, CancellationToken.None);
        var loaded = await repository.LoadAllAsync(CancellationToken.None);

        Assert.Equal(saved.Count, loaded.Count);
        foreach (var expected in saved)
        {
            var actual = Assert.Single(loaded, r => r.RegionId == expected.RegionId && r.TypeId == expected.TypeId);
            Assert.Equal(expected.Stock, actual.Stock); // exact — no tolerance; REAL round-trips bit-identically
            Assert.Equal(expected.Pressure, actual.Pressure);
            Assert.Equal(expected.UpdatedAtTick, actual.UpdatedAtTick);
        }
    }

    [Fact]
    public async Task SaveAllUpsertsOnConflictRatherThanDuplicating()
    {
        using var database = await TestSqliteDatabase.CreateMigratedAsync();
        var repository = new SqliteEcologyRepository(database.ConnectionString);

        await repository.SaveAllAsync([new RegionPopulationRecord("slime_hollow", "slime", 10d, 0d, 100)], CancellationToken.None);
        await repository.SaveAllAsync([new RegionPopulationRecord("slime_hollow", "slime", 4.5d, 6d, 200)], CancellationToken.None);

        var loaded = await repository.LoadAllAsync(CancellationToken.None);

        var row = Assert.Single(loaded);
        Assert.Equal("slime_hollow", row.RegionId);
        Assert.Equal("slime", row.TypeId);
        Assert.Equal(4.5d, row.Stock);
        Assert.Equal(6d, row.Pressure);
        Assert.Equal(200, row.UpdatedAtTick);
    }

    [Fact]
    public async Task SaveAllWithNoRowsIsNoOp()
    {
        using var database = await TestSqliteDatabase.CreateMigratedAsync();
        var repository = new SqliteEcologyRepository(database.ConnectionString);

        await repository.SaveAllAsync([], CancellationToken.None);

        Assert.Empty(await repository.LoadAllAsync(CancellationToken.None));
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, string tableName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "select count(*) from sqlite_master where type = 'table' and name = @name;";
        command.Parameters.AddWithValue("@name", tableName);
        return Convert.ToInt32(await command.ExecuteScalarAsync()) > 0;
    }
}
