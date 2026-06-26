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

    public async Task SavePositionAsync(Guid characterId, WorldVector position, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        // Persist the continuous position (pos_x/pos_y) AND the derived rounded tile (tile_x/tile_y) together —
        // the float columns are the truth login restores; the tile columns are kept coherent so any tile-keyed
        // query still works.
        var tile = position.ToTileRounded();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            update characters
            set pos_x = @pos_x,
                pos_y = @pos_y,
                tile_x = @tile_x,
                tile_y = @tile_y,
                updated_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
            where id = @character_id;
            """;
        command.Parameters.AddWithValue("@pos_x", position.X);
        command.Parameters.AddWithValue("@pos_y", position.Y);
        command.Parameters.AddWithValue("@tile_x", tile.X);
        command.Parameters.AddWithValue("@tile_y", tile.Y);
        command.Parameters.AddWithValue("@character_id", characterId.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ItemStack>> LoadItemsAsync(Guid characterId, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            select template_key, quantity
            from character_items
            where character_id = @character_id
            order by template_key;
            """;
        command.Parameters.AddWithValue("@character_id", characterId.ToString());

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

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        await using var upsert = connection.CreateCommand();
        upsert.Transaction = transaction;
        upsert.CommandText = """
            insert into character_items (character_id, template_key, quantity)
            values (@character_id, @template_key, @quantity)
            on conflict (character_id, template_key)
            do update set quantity = excluded.quantity;
            """;
        var upsertCharacterId = upsert.Parameters.Add("@character_id", Microsoft.Data.Sqlite.SqliteType.Text);
        var upsertTemplateKey = upsert.Parameters.Add("@template_key", Microsoft.Data.Sqlite.SqliteType.Text);
        var upsertQuantity = upsert.Parameters.Add("@quantity", Microsoft.Data.Sqlite.SqliteType.Integer);

        await using var delete = connection.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText = """
            delete from character_items
            where character_id = @character_id and template_key = @template_key;
            """;
        var deleteCharacterId = delete.Parameters.Add("@character_id", Microsoft.Data.Sqlite.SqliteType.Text);
        var deleteTemplateKey = delete.Parameters.Add("@template_key", Microsoft.Data.Sqlite.SqliteType.Text);

        var characterIdText = characterId.ToString();
        foreach (var change in changes)
        {
            if (change.Quantity > 0)
            {
                upsertCharacterId.Value = characterIdText;
                upsertTemplateKey.Value = change.TemplateKey;
                upsertQuantity.Value = change.Quantity;
                await upsert.ExecuteNonQueryAsync(cancellationToken);
            }
            else
            {
                deleteCharacterId.Value = characterIdText;
                deleteTemplateKey.Value = change.TemplateKey;
                await delete.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await transaction.CommitAsync(cancellationToken);
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
            returning account_id, id, display_name, zone_id, tile_x, tile_y, pos_x, pos_y;
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
            ReadPosition(reader, tileXOrdinal: 4, tileYOrdinal: 5, posXOrdinal: 6, posYOrdinal: 7));
    }

    // CONTINUOUS MIGRATION (Phase 10): build the loaded continuous position from pos_x/pos_y, falling back to the
    // tile centre (FromTile(tile_x, tile_y)) if the float columns are null/absent (defensive — the migration
    // backfills them not-null, so the fallback only guards a DB that somehow predates/skipped migration 005).
    private static WorldVector ReadPosition(System.Data.Common.DbDataReader reader, int tileXOrdinal, int tileYOrdinal, int posXOrdinal, int posYOrdinal)
    {
        if (reader.IsDBNull(posXOrdinal) || reader.IsDBNull(posYOrdinal))
        {
            return WorldVector.FromTile(reader.GetInt32(tileXOrdinal), reader.GetInt32(tileYOrdinal));
        }

        return new WorldVector(reader.GetDouble(posXOrdinal), reader.GetDouble(posYOrdinal));
    }

    private static string NormalizeName(string value, string fallback)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        return normalized.Length <= 32 ? normalized : normalized[..32];
    }
}
