using Npgsql;

namespace Mmo.Server.Data;

public sealed class PostgresMigrationRunner : IDatabaseInitializer
{
    private readonly string _connectionString;
    private readonly string _migrationsPath;

    public PostgresMigrationRunner(string connectionString, string migrationsPath)
    {
        _connectionString = connectionString;
        _migrationsPath = migrationsPath;
    }

    public async Task ApplyAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_migrationsPath))
        {
            throw new DirectoryNotFoundException($"Migrations path not found: {_migrationsPath}");
        }

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                create table if not exists schema_migrations (
                    id text primary key,
                    applied_at timestamptz not null default now()
                );
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var migrationFiles = Directory.GetFiles(_migrationsPath, "*.sql")
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);

        foreach (var file in migrationFiles)
        {
            var migrationId = Path.GetFileName(file);
            if (await IsAppliedAsync(connection, migrationId, cancellationToken))
            {
                continue;
            }

            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using (var migrationCommand = connection.CreateCommand())
            {
                migrationCommand.Transaction = transaction;
                migrationCommand.CommandText = await File.ReadAllTextAsync(file, cancellationToken);
                await migrationCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var appliedCommand = connection.CreateCommand())
            {
                appliedCommand.Transaction = transaction;
                appliedCommand.CommandText = "insert into schema_migrations (id) values (@id);";
                appliedCommand.Parameters.AddWithValue("id", migrationId);
                await appliedCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            Runtime.Log.Info($"Applied migration {migrationId}.");
        }
    }

    private static async Task<bool> IsAppliedAsync(
        NpgsqlConnection connection,
        string migrationId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "select exists(select 1 from schema_migrations where id = @id);";
        command.Parameters.AddWithValue("id", migrationId);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is true;
    }
}
