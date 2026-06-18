using Microsoft.Data.Sqlite;
using Mmo.Shared.Domain;

namespace Mmo.Server.Data;

public sealed class SqliteCharacterRepository : ICharacterRepository
{
    private readonly string _connectionString;

    public SqliteCharacterRepository(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task<CharacterRecord> LoadOrCreateAsync(
        string accountName,
        string displayName,
        CancellationToken cancellationToken)
    {
        var safeAccountName = NormalizeName(accountName, "guest");
        var safeDisplayName = NormalizeName(displayName, safeAccountName);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var accountId = await UpsertAccountAsync(connection, transaction, safeAccountName, cancellationToken);
        var character = await UpsertCharacterAsync(connection, transaction, accountId, safeDisplayName, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return character;
    }

    public async Task SavePositionAsync(Guid characterId, TileCoord tile, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            update characters
            set tile_x = @tile_x,
                tile_y = @tile_y,
                updated_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
            where id = @character_id;
            """;
        command.Parameters.AddWithValue("@tile_x", tile.X);
        command.Parameters.AddWithValue("@tile_y", tile.Y);
        command.Parameters.AddWithValue("@character_id", characterId.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<Guid> UpsertAccountAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string accountName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            insert into accounts (id, dev_name)
            values (@id, @dev_name)
            on conflict (dev_name)
            do update set updated_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
            returning id;
            """;
        command.Parameters.AddWithValue("@id", Guid.NewGuid().ToString());
        command.Parameters.AddWithValue("@dev_name", accountName);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is string id ? Guid.Parse(id) : throw new InvalidOperationException("Account upsert did not return an id.");
    }

    private static async Task<CharacterRecord> UpsertCharacterAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid accountId,
        string displayName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            insert into characters (id, account_id, display_name)
            values (@id, @account_id, @display_name)
            on conflict (account_id, display_name)
            do update set updated_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
            returning account_id, id, display_name, zone_id, tile_x, tile_y;
            """;
        command.Parameters.AddWithValue("@id", Guid.NewGuid().ToString());
        command.Parameters.AddWithValue("@account_id", accountId.ToString());
        command.Parameters.AddWithValue("@display_name", displayName);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("Character upsert did not return a row.");
        }

        return new CharacterRecord(
            Guid.Parse(reader.GetString(0)),
            Guid.Parse(reader.GetString(1)),
            reader.GetString(2),
            reader.GetString(3),
            new TileCoord(reader.GetInt32(4), reader.GetInt32(5)));
    }

    private static string NormalizeName(string value, string fallback)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        return normalized.Length <= 32 ? normalized : normalized[..32];
    }
}
