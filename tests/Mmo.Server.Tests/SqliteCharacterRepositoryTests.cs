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
        var savedPosition = new TileCoord(12, 7);

        await repository.SavePositionAsync(character.CharacterId, savedPosition, CancellationToken.None);
        var reloaded = await repository.LoadOrCreateAsync("account-two", "PlayerTwo", CancellationToken.None);

        Assert.Equal(savedPosition, reloaded.Tile);
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
        Assert.Equal(2, count);

        command.CommandText = "select count(*) from accounts;";
        Assert.Equal(0, Convert.ToInt32(await command.ExecuteScalarAsync()));
    }

}
