namespace Mmo.Server.Configuration;

public sealed record ServerOptions(
    int Port,
    int TickRate,
    string ConnectionKey,
    DatabaseProvider DatabaseProvider,
    string ConnectionString,
    string MigrationsPath,
    int WorldWidthTiles,
    int WorldHeightTiles,
    int StepCooldownMs,
    float InterestRadius,
    int MaxVisibleEntities,
    SpawnDistribution SpawnDistribution,
    IReadOnlySet<string> AdminNames)
{
    public TimeSpan StepCooldown => TimeSpan.FromMilliseconds(StepCooldownMs);

    public uint StepCooldownTicks => (uint)Math.Max(1, (int)Math.Ceiling(StepCooldownMs / (1000d / TickRate)));

    public static ServerOptions FromEnvironment()
    {
        var options = new ServerOptions(
            ReadInt("MMO_PORT", 7777),
            ReadInt("MMO_TICK_RATE", 20),
            ReadString("MMO_CONNECTION_KEY", "local-dev"),
            ReadDatabaseProvider("MMO_DB_PROVIDER", DatabaseProvider.Sqlite),
            ResolveConnectionString(ReadString("MMO_DB", "Data Source=data/mmo.db")),
            ResolveMigrationsPath(ReadString("MMO_MIGRATIONS_PATH", "db/sqlite")),
            ReadInt("MMO_WORLD_WIDTH_TILES", 128),
            ReadInt("MMO_WORLD_HEIGHT_TILES", 128),
            ReadInt("MMO_STEP_COOLDOWN_MS", 140),
            ReadFloat("MMO_INTEREST_RADIUS", 40f),
            ReadInt("MMO_MAX_VISIBLE_ENTITIES", 150),
            ReadSpawnDistribution("MMO_SPAWN_DISTRIBUTION", SpawnDistribution.Distributed),
            ReadSet("MMO_ADMIN_NAMES", "Admin"));

        options.Validate();
        return options;
    }

    private void Validate()
    {
        if (Port < 1 || Port > 65535)
        {
            throw new InvalidOperationException("MMO_PORT must be between 1 and 65535.");
        }

        if (TickRate < 1 || TickRate > 120)
        {
            throw new InvalidOperationException("MMO_TICK_RATE must be between 1 and 120.");
        }

        if (string.IsNullOrWhiteSpace(ConnectionKey))
        {
            throw new InvalidOperationException("MMO_CONNECTION_KEY cannot be empty.");
        }

        if (WorldWidthTiles < 16 || WorldWidthTiles > short.MaxValue)
        {
            throw new InvalidOperationException($"MMO_WORLD_WIDTH_TILES must be between 16 and {short.MaxValue}.");
        }

        if (WorldHeightTiles < 16 || WorldHeightTiles > short.MaxValue)
        {
            throw new InvalidOperationException($"MMO_WORLD_HEIGHT_TILES must be between 16 and {short.MaxValue}.");
        }

        if (StepCooldownMs < 50 || StepCooldownMs > 5000)
        {
            throw new InvalidOperationException("MMO_STEP_COOLDOWN_MS must be between 50 and 5000.");
        }

        if (InterestRadius <= 0)
        {
            throw new InvalidOperationException("MMO_INTEREST_RADIUS must be greater than zero.");
        }

        if (MaxVisibleEntities < 1 || MaxVisibleEntities > 4096)
        {
            throw new InvalidOperationException("MMO_MAX_VISIBLE_ENTITIES must be between 1 and 4096.");
        }

        if (string.IsNullOrWhiteSpace(ConnectionString))
        {
            throw new InvalidOperationException("MMO_DB cannot be empty.");
        }

        if (string.IsNullOrWhiteSpace(MigrationsPath))
        {
            throw new InvalidOperationException("MMO_MIGRATIONS_PATH cannot be empty.");
        }
    }

    private static string ReadString(string key, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(key);
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private static int ReadInt(string key, int fallback)
    {
        var value = Environment.GetEnvironmentVariable(key);
        return int.TryParse(value, out var parsed) ? parsed : fallback;
    }

    private static float ReadFloat(string key, float fallback)
    {
        var value = Environment.GetEnvironmentVariable(key);
        return float.TryParse(value, out var parsed) ? parsed : fallback;
    }

    private static IReadOnlySet<string> ReadSet(string key, string fallback)
    {
        var value = ReadString(key, fallback);
        return value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(item => item.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static DatabaseProvider ReadDatabaseProvider(string key, DatabaseProvider fallback)
    {
        var value = Environment.GetEnvironmentVariable(key);
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "sqlite" => DatabaseProvider.Sqlite,
            "postgres" or "postgresql" => DatabaseProvider.Postgres,
            _ => fallback
        };
    }

    private static SpawnDistribution ReadSpawnDistribution(string key, SpawnDistribution fallback)
    {
        var value = Environment.GetEnvironmentVariable(key);
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "distributed" => SpawnDistribution.Distributed,
            "clustered" or "cluster" => SpawnDistribution.Clustered,
            _ => fallback
        };
    }

    private static string ResolveConnectionString(string connectionString)
    {
        const string dataSourcePrefix = "Data Source=";
        if (!connectionString.StartsWith(dataSourcePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return connectionString;
        }

        var path = connectionString[dataSourcePrefix.Length..];
        if (Path.IsPathRooted(path))
        {
            return connectionString;
        }

        return $"{dataSourcePrefix}{ResolveProjectPath(path)}";
    }

    private static string ResolveMigrationsPath(string configuredPath)
    {
        if (Path.IsPathRooted(configuredPath))
        {
            return configuredPath;
        }

        return ResolveProjectPath(configuredPath);
    }

    private static string ResolveProjectPath(string configuredPath)
    {
        var current = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, configuredPath);
            if (Directory.Exists(candidate) || File.Exists(candidate))
            {
                return candidate;
            }

            if (File.Exists(Path.Combine(current.FullName, "Mmo.sln")))
            {
                return Path.Combine(current.FullName, configuredPath);
            }

            current = current.Parent;
        }

        return Path.GetFullPath(configuredPath);
    }
}
