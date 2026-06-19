using Microsoft.Data.Sqlite;
using Mmo.Server.Data;
using Mmo.Shared.Domain;
using Xunit;

namespace Mmo.Server.Tests;

public sealed class SqliteCharacterItemsTests
{
    [Fact]
    public async Task SaveAndLoadItemsRoundTrips()
    {
        using var database = await TestSqliteDatabase.CreateMigratedAsync();
        var repository = new SqliteCharacterRepository(database.ConnectionString);
        var character = await repository.LoadOrCreateAsync("acct-items", "ItemPlayer", CancellationToken.None);

        await repository.SaveItemsAsync(
            character.CharacterId,
            [new ItemStack("wood", 12), new ItemStack("stone", 5)],
            CancellationToken.None);

        var loaded = await repository.LoadItemsAsync(character.CharacterId, CancellationToken.None);

        Assert.Equal(2, loaded.Count);
        Assert.Contains(loaded, s => s.TemplateKey == "wood" && s.Quantity == 12);
        Assert.Contains(loaded, s => s.TemplateKey == "stone" && s.Quantity == 5);
    }

    [Fact]
    public async Task SaveItemsUpsertsChangedQuantitiesAndDeletesEmptied()
    {
        using var database = await TestSqliteDatabase.CreateMigratedAsync();
        var repository = new SqliteCharacterRepository(database.ConnectionString);
        var character = await repository.LoadOrCreateAsync("acct-upsert", "UpsertPlayer", CancellationToken.None);

        await repository.SaveItemsAsync(
            character.CharacterId,
            [new ItemStack("wood", 3), new ItemStack("stone", 7)],
            CancellationToken.None);

        // Upsert wood to a new quantity; delete stone (quantity 0).
        await repository.SaveItemsAsync(
            character.CharacterId,
            [new ItemStack("wood", 10), new ItemStack("stone", 0)],
            CancellationToken.None);

        var loaded = await repository.LoadItemsAsync(character.CharacterId, CancellationToken.None);

        var stack = Assert.Single(loaded);
        Assert.Equal("wood", stack.TemplateKey);
        Assert.Equal(10, stack.Quantity);
    }

    [Fact]
    public async Task LoadItemsReturnsEmptyForCharacterWithNoItems()
    {
        using var database = await TestSqliteDatabase.CreateMigratedAsync();
        var repository = new SqliteCharacterRepository(database.ConnectionString);
        var character = await repository.LoadOrCreateAsync("acct-empty", "EmptyPlayer", CancellationToken.None);

        var loaded = await repository.LoadItemsAsync(character.CharacterId, CancellationToken.None);

        Assert.Empty(loaded);
    }

    [Fact]
    public async Task SaveItemsWithNoChangesIsNoOp()
    {
        using var database = await TestSqliteDatabase.CreateMigratedAsync();
        var repository = new SqliteCharacterRepository(database.ConnectionString);
        var character = await repository.LoadOrCreateAsync("acct-noop", "NoopPlayer", CancellationToken.None);

        await repository.SaveItemsAsync(character.CharacterId, [], CancellationToken.None);

        Assert.Empty(await repository.LoadItemsAsync(character.CharacterId, CancellationToken.None));
    }

    [Fact]
    public async Task CleanBootstrapCreatesCharacterItemsTable()
    {
        using var database = TestSqliteDatabase.CreateEmpty();
        var migrations = new SqliteMigrationRunner(database.ConnectionString, TestSqliteDatabase.MigrationsPath);

        await migrations.ApplyAsync(CancellationToken.None);

        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync();
        Assert.True(await TableExistsAsync(connection, "character_items"));
    }

    [Fact]
    public async Task ExistingDatabaseUpgradeAddsCharacterItemsWithoutDataLoss()
    {
        using var database = TestSqliteDatabase.CreateEmpty();
        var legacyMigrationsPath = CreateLegacyMigrationsPath();

        try
        {
            // Stand up a DB at the pre-S37 schema (migrations 001-003, no character_items).
            var legacyMigrations = new SqliteMigrationRunner(database.ConnectionString, legacyMigrationsPath);
            await legacyMigrations.ApplyAsync(CancellationToken.None);

            var legacyRepository = new SqliteCharacterRepository(database.ConnectionString);
            var character = await legacyRepository.LoadOrCreateAsync("legacy-acct", "LegacyPlayer", CancellationToken.None);
            await legacyRepository.SaveTileAsync(character.CharacterId, new TileCoord(5, 9), CancellationToken.None);

            await using (var connection = new SqliteConnection(database.ConnectionString))
            {
                await connection.OpenAsync();
                Assert.False(await TableExistsAsync(connection, "character_items"));
            }

            // Apply the current migration set (adds 004_character_items) over the populated DB.
            var currentMigrations = new SqliteMigrationRunner(database.ConnectionString, TestSqliteDatabase.MigrationsPath);
            await currentMigrations.ApplyAsync(CancellationToken.None);

            await using (var connection = new SqliteConnection(database.ConnectionString))
            {
                await connection.OpenAsync();
                Assert.True(await TableExistsAsync(connection, "character_items"));
            }

            // Pre-existing character data survived the upgrade, and the new table is usable.
            var reloaded = await legacyRepository.LoadOrCreateAsync("legacy-acct", "LegacyPlayer", CancellationToken.None);
            Assert.Equal(character.CharacterId, reloaded.CharacterId);
            Assert.Equal(new TileCoord(5, 9), reloaded.Tile);

            await legacyRepository.SaveItemsAsync(reloaded.CharacterId, [new ItemStack("fiber", 4)], CancellationToken.None);
            var items = await legacyRepository.LoadItemsAsync(reloaded.CharacterId, CancellationToken.None);
            Assert.Equal(new ItemStack("fiber", 4), Assert.Single(items));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(legacyMigrationsPath)!, recursive: true);
        }
    }

    private static string CreateLegacyMigrationsPath()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "sqlite-tests", Guid.NewGuid().ToString("N"), "migrations");
        Directory.CreateDirectory(directory);

        foreach (var fileName in new[] { "001_initial.sql", "002_tile_positions.sql", "003_drop_position_columns.sql" })
        {
            File.Copy(
                Path.Combine(TestSqliteDatabase.MigrationsPath, fileName),
                Path.Combine(directory, fileName));
        }

        return directory;
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, string tableName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "select count(*) from sqlite_master where type = 'table' and name = @name;";
        command.Parameters.AddWithValue("@name", tableName);
        return Convert.ToInt32(await command.ExecuteScalarAsync()) > 0;
    }
}
