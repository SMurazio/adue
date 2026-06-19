using Mmo.Shared.Domain;
using Npgsql;

namespace Mmo.Server.Data;

public sealed class PostgresCharacterRepository : ICharacterRepository
{
    private readonly string _connectionString;

    public PostgresCharacterRepository(string connectionString)
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

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var accountId = await UpsertAccountAsync(connection, transaction, safeAccountName, cancellationToken);
        var character = await UpsertCharacterAsync(connection, transaction, accountId, safeDisplayName, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return character;
    }

    public async Task SaveTileAsync(Guid characterId, TileCoord tile, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            update characters
            set tile_x = @tile_x,
                tile_y = @tile_y,
                updated_at = now()
            where id = @character_id;
            """;
        command.Parameters.AddWithValue("tile_x", tile.X);
        command.Parameters.AddWithValue("tile_y", tile.Y);
        command.Parameters.AddWithValue("character_id", characterId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ItemStack>> LoadItemsAsync(Guid characterId, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            select template_key, quantity
            from character_items
            where character_id = @character_id
            order by template_key;
            """;
        command.Parameters.AddWithValue("character_id", characterId);

        var stacks = new List<ItemStack>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            stacks.Add(new ItemStack(reader.GetString(0), reader.GetInt32(1)));
        }

        return stacks;
    }

    public async Task SaveItemsAsync(Guid characterId, IReadOnlyList<ItemStack> changes, CancellationToken cancellationToken)
    {
        if (changes.Count == 0)
        {
            return;
        }

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using var upsert = connection.CreateCommand();
        upsert.Transaction = transaction;
        upsert.CommandText = """
            insert into character_items (character_id, template_key, quantity)
            values (@character_id, @template_key, @quantity)
            on conflict (character_id, template_key)
            do update set quantity = excluded.quantity;
            """;
        var upsertCharacterId = upsert.Parameters.Add("character_id", NpgsqlTypes.NpgsqlDbType.Uuid);
        var upsertTemplateKey = upsert.Parameters.Add("template_key", NpgsqlTypes.NpgsqlDbType.Text);
        var upsertQuantity = upsert.Parameters.Add("quantity", NpgsqlTypes.NpgsqlDbType.Integer);

        await using var delete = connection.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText = """
            delete from character_items
            where character_id = @character_id and template_key = @template_key;
            """;
        var deleteCharacterId = delete.Parameters.Add("character_id", NpgsqlTypes.NpgsqlDbType.Uuid);
        var deleteTemplateKey = delete.Parameters.Add("template_key", NpgsqlTypes.NpgsqlDbType.Text);

        foreach (var change in changes)
        {
            if (change.Quantity > 0)
            {
                upsertCharacterId.Value = characterId;
                upsertTemplateKey.Value = change.TemplateKey;
                upsertQuantity.Value = change.Quantity;
                await upsert.ExecuteNonQueryAsync(cancellationToken);
            }
            else
            {
                deleteCharacterId.Value = characterId;
                deleteTemplateKey.Value = change.TemplateKey;
                await delete.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task<Guid> UpsertAccountAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string accountName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            insert into accounts (dev_name)
            values (@dev_name)
            on conflict (dev_name)
            do update set updated_at = now()
            returning id;
            """;
        command.Parameters.AddWithValue("dev_name", accountName);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is Guid id ? id : throw new InvalidOperationException("Account upsert did not return an id.");
    }

    private static async Task<CharacterRecord> UpsertCharacterAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid accountId,
        string displayName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            insert into characters (account_id, display_name)
            values (@account_id, @display_name)
            on conflict (account_id, display_name)
            do update set updated_at = now()
            returning account_id, id, display_name, zone_id, tile_x, tile_y;
            """;
        command.Parameters.AddWithValue("account_id", accountId);
        command.Parameters.AddWithValue("display_name", displayName);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("Character upsert did not return a row.");
        }

        return new CharacterRecord(
            reader.GetGuid(0),
            reader.GetGuid(1),
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
