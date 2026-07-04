using Microsoft.Data.Sqlite;
using Mmo.Server.Data;
using Mmo.Shared.Domain;
using Xunit;

namespace Mmo.Server.Tests;

public sealed class SqliteCharacterRepositoryTests
{
    [Fact]
    public async Task LoadOrCreateIsIdempotentForSameAccountAndDisplayName()
    {
        using var database = await TestSqliteDatabase.CreateMigratedAsync();
        var repository = new SqliteCharacterRepository(database.ConnectionString);

        var first = await repository.LoadOrCreateAsync("account-one", "PlayerOne", CancellationToken.None);
        var second = await repository.LoadOrCreateAsync("account-one", "PlayerOne", CancellationToken.None);

        Assert.Equal(first.AccountId, second.AccountId);
        Assert.Equal(first.CharacterId, second.CharacterId);
        Assert.Equal("PlayerOne", second.DisplayName);
        Assert.Equal("sandbox", second.ZoneId);
        Assert.Equal(new TileCoord(8, 8), second.Tile);
    }

    [Fact]
    public async Task SavePositionPersistsForSubsequentLoad()
    {
        using var database = await TestSqliteDatabase.CreateMigratedAsync();
        var repository = new SqliteCharacterRepository(database.ConnectionString);
        var character = await repository.LoadOrCreateAsync("account-two", "PlayerTwo", CancellationToken.None);
        var savedPosition = WorldVector.FromTile(new TileCoord(12, 7));

        await repository.SavePositionAsync(character.CharacterId, savedPosition, CancellationToken.None);
        var reloaded = await repository.LoadOrCreateAsync("account-two", "PlayerTwo", CancellationToken.None);

        Assert.Equal(savedPosition, reloaded.Position);
        Assert.Equal(new TileCoord(12, 7), reloaded.Tile);
    }

    // CONTINUOUS MIGRATION (Phase 10): a SUB-TILE continuous position must round-trip losslessly (to float
    // precision) through save+load — the exact thing the old integer tile_x/tile_y columns could NOT store
    // (they snapped (10.4, 8.7) to the (10, 9) tile centre on relog). pos_x/pos_y are double precision, so the
    // reloaded Position must equal the saved one within a tight tolerance.
    [Fact]
    public async Task SaveSubTilePositionRoundTrips()
    {
        using var database = await TestSqliteDatabase.CreateMigratedAsync();
        var repository = new SqliteCharacterRepository(database.ConnectionString);
        var character = await repository.LoadOrCreateAsync("account-subtile", "SubTilePlayer", CancellationToken.None);
        var savedPosition = new WorldVector(10.4d, 8.7d);

        await repository.SavePositionAsync(character.CharacterId, savedPosition, CancellationToken.None);
        var reloaded = await repository.LoadOrCreateAsync("account-subtile", "SubTilePlayer", CancellationToken.None);

        Assert.Equal(savedPosition.X, reloaded.Position.X, precision: 9);
        Assert.Equal(savedPosition.Y, reloaded.Position.Y, precision: 9);
        // The derived tile is the NEAREST centre — (10.4, 8.7) rounds to (10, 9). The point of the float columns
        // is that the sub-tile offset survives even though the tile rounds away from it.
        Assert.Equal(new TileCoord(10, 9), reloaded.Tile);
    }

    [Fact]
    public async Task MigrationBootstrapAndExistingDatabaseReapplyAreIdempotent()
    {
        using var database = TestSqliteDatabase.CreateEmpty();
        var migrations = new SqliteMigrationRunner(database.ConnectionString, TestSqliteDatabase.MigrationsPath);

        await migrations.ApplyAsync(CancellationToken.None);
        await migrations.ApplyAsync(CancellationToken.None);

        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "select count(*) from schema_migrations;";
        var count = Convert.ToInt32(await command.ExecuteScalarAsync());
        Assert.Equal(6, count); // 006_region_populations (ecology E3) joined the set.

        command.CommandText = "select count(*) from accounts;";
        Assert.Equal(0, Convert.ToInt32(await command.ExecuteScalarAsync()));

        Assert.False(await ColumnExistsAsync(connection, "position_x"));
        Assert.False(await ColumnExistsAsync(connection, "position_y"));
        Assert.True(await ColumnExistsAsync(connection, "tile_x"));
        Assert.True(await ColumnExistsAsync(connection, "tile_y"));
        // Phase 10: the continuous float position columns exist alongside the kept tile columns.
        Assert.True(await ColumnExistsAsync(connection, "pos_x"));
        Assert.True(await ColumnExistsAsync(connection, "pos_y"));
    }

    [Fact]
    public async Task ExistingTilePositionDatabaseUpgradeDropsLegacyPositionColumns()
    {
        using var database = TestSqliteDatabase.CreateEmpty();
        var legacyMigrationsPath = CreateLegacyTileMigrationsPath();

        try
        {
            var legacyMigrations = new SqliteMigrationRunner(database.ConnectionString, legacyMigrationsPath);
            await legacyMigrations.ApplyAsync(CancellationToken.None);

            await using (var connection = new SqliteConnection(database.ConnectionString))
            {
                await connection.OpenAsync();
                Assert.True(await ColumnExistsAsync(connection, "position_x"));
                Assert.True(await ColumnExistsAsync(connection, "position_y"));
                Assert.True(await ColumnExistsAsync(connection, "tile_x"));
                Assert.True(await ColumnExistsAsync(connection, "tile_y"));
            }

            var currentMigrations = new SqliteMigrationRunner(database.ConnectionString, TestSqliteDatabase.MigrationsPath);
            await currentMigrations.ApplyAsync(CancellationToken.None);

            await using (var connection = new SqliteConnection(database.ConnectionString))
            {
                await connection.OpenAsync();
                Assert.False(await ColumnExistsAsync(connection, "position_x"));
                Assert.False(await ColumnExistsAsync(connection, "position_y"));
                Assert.True(await ColumnExistsAsync(connection, "tile_x"));
                Assert.True(await ColumnExistsAsync(connection, "tile_y"));
            }
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(legacyMigrationsPath)!, recursive: true);
        }
    }

    private static string CreateLegacyTileMigrationsPath()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "sqlite-tests", Guid.NewGuid().ToString("N"), "migrations");
        Directory.CreateDirectory(directory);

        foreach (var fileName in new[] { "001_initial.sql", "002_tile_positions.sql" })
        {
            File.Copy(
                Path.Combine(TestSqliteDatabase.MigrationsPath, fileName),
                Path.Combine(directory, fileName));
        }

        return directory;
    }

    private static async Task<bool> ColumnExistsAsync(SqliteConnection connection, string columnName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "select count(*) from pragma_table_info('characters') where name = @name;";
        command.Parameters.AddWithValue("@name", columnName);
        return Convert.ToInt32(await command.ExecuteScalarAsync()) > 0;
    }
}
