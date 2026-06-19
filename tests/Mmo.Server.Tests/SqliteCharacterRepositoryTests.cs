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
    public async Task SaveTilePersistsForSubsequentLoad()
    {
        using var database = await TestSqliteDatabase.CreateMigratedAsync();
        var repository = new SqliteCharacterRepository(database.ConnectionString);
        var character = await repository.LoadOrCreateAsync("account-two", "PlayerTwo", CancellationToken.None);
        var savedTile = new TileCoord(12, 7);

        await repository.SaveTileAsync(character.CharacterId, savedTile, CancellationToken.None);
        var reloaded = await repository.LoadOrCreateAsync("account-two", "PlayerTwo", CancellationToken.None);

        Assert.Equal(savedTile, reloaded.Tile);
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
        Assert.Equal(4, count);

        command.CommandText = "select count(*) from accounts;";
        Assert.Equal(0, Convert.ToInt32(await command.ExecuteScalarAsync()));

        Assert.False(await ColumnExistsAsync(connection, "position_x"));
        Assert.False(await ColumnExistsAsync(connection, "position_y"));
        Assert.True(await ColumnExistsAsync(connection, "tile_x"));
        Assert.True(await ColumnExistsAsync(connection, "tile_y"));
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
