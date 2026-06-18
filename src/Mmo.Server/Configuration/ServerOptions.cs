namespace Mmo.Server.Configuration;

public sealed record ServerOptions(
    int Port,
    int TickRate,
    string ConnectionKey,
    DatabaseProvider DatabaseProvider,
    string ConnectionString,
    string MigrationsPath,
    float MovementUnitsPerSecond,
    float InterestRadius,
    int MaxVisibleEntities,
    IReadOnlySet<string> AdminNames)
{
    public static ServerOptions FromEnvironment()
    {
        var options = new ServerOptions(
            ReadInt("MMO_PORT", 7777),
            ReadInt("MMO_TICK_RATE", 20),
            ReadString("MMO_CONNECTION_KEY", "local-dev"),
            ReadDatabaseProvider("MMO_DB_PROVIDER", DatabaseProvider.Sqlite),
            ResolveConnectionString(ReadString("MMO_DB", "Data Source=data/mmo.db")),
            ResolveMigrationsPath(ReadString("MMO_MIGRATIONS_PATH", "db/sqlite")),
            ReadFloat("MMO_MOVE_SPEED", 5f),
            ReadFloat("MMO_INTEREST_RADIUS", 96f),
            ReadInt("MMO_MAX_VISIBLE_ENTITIES", 150),
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

        if (MovementUnitsPerSecond <= 0)
        {
            throw new InvalidOperationException("MMO_MOVE_SPEED must be greater than zero.");
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
