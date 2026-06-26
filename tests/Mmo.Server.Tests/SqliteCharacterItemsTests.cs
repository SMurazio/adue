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

            // This DB is at the pre-S37 schema (001-003): it has tile_x/tile_y but NOT the Phase-10 pos_x/pos_y
            // columns. The production repo's LoadOrCreate/SavePosition now reference pos_x (insert ... returning
            // pos_x, save writes pos_x), so neither can run against this pre-005 schema yet. Seed the account +
            // character row (at tile 5,9) DIRECTLY to mimic a legacy DB, then verify it survives the 004 (and,
            // transitively, 005) upgrade applied below — at which point the production repo can read it again.
            var characterId = await SeedLegacyCharacterAsync(
                database.ConnectionString, "legacy-acct", "LegacyPlayer", new TileCoord(5, 9));

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

            // Pre-existing character data survived the upgrade, and the new table is usable. The production repo
            // works again now that 005 added pos_x/pos_y (its returning clause references them).
            var repository = new SqliteCharacterRepository(database.ConnectionString);
            var reloaded = await repository.LoadOrCreateAsync("legacy-acct", "LegacyPlayer", CancellationToken.None);
            Assert.Equal(characterId, reloaded.CharacterId);
            Assert.Equal(new TileCoord(5, 9), reloaded.Tile);

            await repository.SaveItemsAsync(reloaded.CharacterId, [new ItemStack("fiber", 4)], CancellationToken.None);
            var items = await repository.LoadItemsAsync(reloaded.CharacterId, CancellationToken.None);
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

    // Inserts an account + character row DIRECTLY against the 001-003 legacy schema (tile_x/tile_y present, no
    // pos_x/pos_y). The production SqliteCharacterRepository can't create here — its upsert returns pos_x, which
    // this pre-005 schema lacks — so we hand-write the legacy rows to mimic a DB that predates Phase 10, then
    // verify they survive the 004/005 upgrade. Returns the new character id.
    private static async Task<Guid> SeedLegacyCharacterAsync(
        string connectionString, string accountName, string displayName, TileCoord tile)
    {
        var accountId = Guid.NewGuid();
        var characterId = Guid.NewGuid();

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        await using (var accountCommand = connection.CreateCommand())
        {
            accountCommand.CommandText = "insert into accounts (id, dev_name) values (@id, @dev_name);";
            accountCommand.Parameters.AddWithValue("@id", accountId.ToString());
            accountCommand.Parameters.AddWithValue("@dev_name", accountName);
            await accountCommand.ExecuteNonQueryAsync();
        }

        await using (var characterCommand = connection.CreateCommand())
        {
            characterCommand.CommandText = """
                insert into characters (id, account_id, display_name, tile_x, tile_y)
                values (@id, @account_id, @display_name, @tile_x, @tile_y);
                """;
            characterCommand.Parameters.AddWithValue("@id", characterId.ToString());
            characterCommand.Parameters.AddWithValue("@account_id", accountId.ToString());
            characterCommand.Parameters.AddWithValue("@display_name", displayName);
            characterCommand.Parameters.AddWithValue("@tile_x", tile.X);
            characterCommand.Parameters.AddWithValue("@tile_y", tile.Y);
            await characterCommand.ExecuteNonQueryAsync();
        }

        return characterId;
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, string tableName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "select count(*) from sqlite_master where type = 'table' and name = @name;";
        command.Parameters.AddWithValue("@name", tableName);
        return Convert.ToInt32(await command.ExecuteScalarAsync()) > 0;
    }
}
