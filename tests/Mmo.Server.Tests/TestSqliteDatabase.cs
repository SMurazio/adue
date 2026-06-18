using Microsoft.Data.Sqlite;
using Mmo.Server.Data;

namespace Mmo.Server.Tests;

internal sealed class TestSqliteDatabase : IDisposable
{
    private readonly string _directory;

    private TestSqliteDatabase(string directory, string connectionString)
    {
        _directory = directory;
        ConnectionString = connectionString;
    }

    public string ConnectionString { get; }

    public static string MigrationsPath => FindMigrationsPath();

    public static TestSqliteDatabase CreateEmpty()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "sqlite-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, "mmo.db");
        var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
        return new TestSqliteDatabase(directory, connectionString);
    }

    public static async Task<TestSqliteDatabase> CreateMigratedAsync()
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
