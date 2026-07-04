using Microsoft.Data.Sqlite;

namespace Mmo.Server.Data;

// ECOLOGY E3 (docs/ecology-v1-design.md D8, S8 E3): the SQLite-backed IEcologyRepository, mirroring
// SqliteCharacterRepository's connection-per-call pattern (a fresh SqliteConnection per method, no pooling of
// our own — Microsoft.Data.Sqlite pools connections internally per connection string).
public sealed class SqliteEcologyRepository : IEcologyRepository
{
    private readonly string _connectionString;

    public SqliteEcologyRepository(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task<IReadOnlyList<RegionPopulationRecord>> LoadAllAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            select region_id, type_id, stock, pressure, updated_at_tick
            from region_populations;
            """;

        var rows = new List<RegionPopulationRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            // Corruption guard: SQLite's type affinity lets a REAL column hold NULL (which is also what a
            // hand-edited NaN degrades to — the driver refuses to WRITE NaN at all), and GetDouble on NULL would
            // throw and kill the whole load. A row with any NULL field is untrustworthy: skip it and let the
            // boot loader keep that region×type's K seed (same policy as its non-finite rejection).
            if (reader.IsDBNull(0) || reader.IsDBNull(1) || reader.IsDBNull(2) || reader.IsDBNull(3) || reader.IsDBNull(4))
            {
                continue;
            }

            rows.Add(new RegionPopulationRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetDouble(2),
                reader.GetDouble(3),
                reader.GetInt64(4)));
        }

        return rows;
    }

    public async Task SaveAllAsync(IReadOnlyList<RegionPopulationRecord> rows, CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return;
        }

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        // Single transaction for the whole batch (S5.3's "save -> load bit-identical" acceptance needs every
        // region x type to land together, not partially — mirrors SqliteCharacterRepository.SaveItemsAsync's
        // one-transaction-per-batch shape).
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        await using var upsert = connection.CreateCommand();
        upsert.Transaction = transaction;
        upsert.CommandText = """
            insert into region_populations (region_id, type_id, stock, pressure, updated_at_tick)
            values (@region_id, @type_id, @stock, @pressure, @updated_at_tick)
            on conflict (region_id, type_id)
            do update set stock = excluded.stock, pressure = excluded.pressure, updated_at_tick = excluded.updated_at_tick;
            """;
        var regionIdParam = upsert.Parameters.Add("@region_id", SqliteType.Text);
        var typeIdParam = upsert.Parameters.Add("@type_id", SqliteType.Text);
        var stockParam = upsert.Parameters.Add("@stock", SqliteType.Real);
        var pressureParam = upsert.Parameters.Add("@pressure", SqliteType.Real);
        var updatedAtTickParam = upsert.Parameters.Add("@updated_at_tick", SqliteType.Integer);

        foreach (var row in rows)
        {
            regionIdParam.Value = row.RegionId;
            typeIdParam.Value = row.TypeId;
            stockParam.Value = row.Stock;
            pressureParam.Value = row.Pressure;
            updatedAtTickParam.Value = row.UpdatedAtTick;
            await upsert.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }
}
