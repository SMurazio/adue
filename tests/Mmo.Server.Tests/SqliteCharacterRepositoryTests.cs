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
        using var database = await SqliteTestDatabase.CreateMigratedAsync();
        var repository = new SqliteCharacterRepository(database.ConnectionString);

        var first = await repository.LoadOrCreateAsync("account-one", "PlayerOne", CancellationToken.None);
        var second = await repository.LoadOrCreateAsync("account-one", "PlayerOne", CancellationToken.None);

        Assert.Equal(first.AccountId, second.AccountId);
        Assert.Equal(first.CharacterId, second.CharacterId);
        Assert.Equal("PlayerOne", second.DisplayName);
        Assert.Equal("sandbox", second.ZoneId);
        Assert.Equal(WorldVector.Zero, second.Position);
    }

    [Fact]
    public async Task SavePositionPersistsForSubsequentLoad()
    {
        using var database = await SqliteTestDatabase.CreateMigratedAsync();
        var repository = new SqliteCharacterRepository(database.ConnectionString);
        var character = await repository.LoadOrCreateAsync("account-two", "PlayerTwo", CancellationToken.None);
        var savedPosition = new WorldVector(12.5f, -7.25f);

        await repository.SavePositionAsync(character.CharacterId, savedPosition, CancellationToken.None);
        var reloaded = await repository.LoadOrCreateAsync("account-two", "PlayerTwo", CancellationToken.None);

        Assert.Equal(savedPosition, reloaded.Position);
    }

    [Fact]
    public async Task MigrationBootstrapAndExistingDatabaseReapplyAreIdempotent()
    {
        using var database = SqliteTestDatabase.CreateEmpty();
        var migrations = new SqliteMigrationRunner(database.ConnectionString, SqliteTestDatabase.MigrationsPath);

        await migrations.ApplyAsync(CancellationToken.None);
        await migrations.ApplyAsync(CancellationToken.None);

        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "select count(*) from schema_migrations;";
        var count = Convert.ToInt32(await command.ExecuteScalarAsync());
        Assert.Equal(1, count);

        command.CommandText = "select count(*) from accounts;";
        Assert.Equal(0, Convert.ToInt32(await command.ExecuteScalarAsync()));
    }

    private sealed class SqliteTestDatabase : IDisposable
    {
        private readonly string _directory;

        private SqliteTestDatabase(string directory, string connectionString)
        {
            _directory = directory;
            ConnectionString = connectionString;
        }

        public string ConnectionString { get; }

        public static string MigrationsPath => FindMigrationsPath();

        public static SqliteTestDatabase CreateEmpty()
        {
            var directory = Path.Combine(AppContext.BaseDirectory, "sqlite-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var databasePath = Path.Combine(directory, "mmo.db");
            var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
            return new SqliteTestDatabase(directory, connectionString);
        }

        public static async Task<SqliteTestDatabase> CreateMigratedAsync()
        {
            var database = CreateEmpty();
            var migrations = new SqliteMigrationRunner(database.ConnectionString, MigrationsPath);
            await migrations.ApplyAsync(CancellationToken.None);
            return database;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch
            {
            }
        }

        private static string FindMigrationsPath()
        {
            var current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current is not null)
            {
                var candidate = Path.Combine(current.FullName, "db", "sqlite");
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }

                current = current.Parent;
            }

            throw new DirectoryNotFoundException("Could not find db/sqlite migrations path.");
        }
    }
}
