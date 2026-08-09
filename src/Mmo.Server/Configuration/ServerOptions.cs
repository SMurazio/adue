using Mmo.Server.Runtime;
using Mmo.Shared.Domain;

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
    int PersistenceCheckpointSeconds,
    float InterestRadius,
    int MaxVisibleEntities,
    SpawnDistribution SpawnDistribution,
    IReadOnlySet<string> AdminNames)
{
    // AUTHORED-MAP M3: which terrain genVersion this server generates. The REAL boot path
    // (FromEnvironment) defaults to TerrainGenerator.CurrentGenVersion — the authored 384x384
    // town+floor-1 map — and derives the world-size defaults from the authored grid; MMO_GEN_VERSION=1
    // restores the legacy procedural layout (128x128 defaults). This init-only property itself
    // DEFAULTS TO 1 (procedural) so the many hand-constructed test ServerOptions — which pick
    // arbitrary small world sizes — keep meaning "procedural at my stated size": an authored
    // genVersion only accepts width/height equal to the authored grid's dims (the shared generator
    // fails loudly otherwise; Validate pre-empts that with the env-var names on the boot path).
    public int GenVersion { get; init; } = 1;

    // Procedural map seed. Default 0 (TileGrid.DefaultSeed) keeps the generated map — and therefore
    // persisted tile positions — stable across restarts. Init-only (not a positional ctor arg) so the
    // many test constructions of ServerOptions don't all have to thread it. (Unused — but harmless —
    // under an authored genVersion: an authored layout has no randomness to seed.)
    public int MapSeed { get; init; }

    // ADUE P2 (todo/S-p2-auto-pair-and-duo-reveal.md): the two-player in-person DEMO mode. OFF by default so every
    // existing test + dev/headless flow is byte-unchanged (solo-start + `/pair` survive). When ON: a joining player is
    // AUTO-PAIRED with the one other unpaired online player (pairing stops being a typed input), and an UNPAIRED ready
    // is REFUSED ("Waiting for your partner to join.") instead of solo-starting — closing the operator-intervention
    // race where P1 mashes ready before P2's client has connected. Enabled on the demo launch via MMO_DEMO_MODE (the
    // duo front door sets it; solo dev leaves it unset). Init-only so the many test ServerOptions don't thread it.
    public bool DemoMode { get; init; }

    public bool DebugMovement { get; init; }

    public IReadOnlySet<string> DebugMovementWatchNames { get; init; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public double DebugMovementHitchThresholdMultiplier { get; init; } = 1.5d;

    public double DebugMovementTickDurationThresholdMs { get; init; } = 15d;

    public TimeSpan StepCooldown => TimeSpan.FromMilliseconds(StepCooldownMs);

    public uint StepCooldownTicks => (uint)Math.Max(1, (int)Math.Ceiling(StepCooldownMs / (1000d / TickRate)));

    public TimeSpan PersistenceCheckpointInterval => TimeSpan.FromSeconds(PersistenceCheckpointSeconds);

    public uint PersistenceCheckpointTicks => (uint)Math.Max(1, PersistenceCheckpointSeconds * TickRate);

    public static ServerOptions FromEnvironment()
    {
        // AUTHORED-MAP M3: resolve the genVersion FIRST — the world-size defaults follow from it. An
        // authored map's dims are content, not config, so the defaults come FROM the authored grid
        // and the two can never drift; an explicit MMO_WORLD_*_TILES still applies but must match the
        // grid (Validate fails loudly with the env-var names). MMO_GEN_VERSION=1 = the legacy
        // procedural map with the historical 128x128 defaults.
        var genVersion = ReadInt("MMO_GEN_VERSION", TerrainGenerator.CurrentGenVersion);
        var authored = genVersion == TerrainGenerator.AuthoredGenVersion;
        var defaultWorldWidth = authored ? AuthoredMaps.TownAndFloor1Width : 128;
        var defaultWorldHeight = authored ? AuthoredMaps.TownAndFloor1Height : 128;

        var options = new ServerOptions(
            ReadInt("MMO_PORT", 7777),
            ReadInt("MMO_TICK_RATE", 20),
            ReadString("MMO_CONNECTION_KEY", "local-dev"),
            ReadDatabaseProvider("MMO_DB_PROVIDER", DatabaseProvider.Sqlite),
            ResolveConnectionString(ReadString("MMO_DB", "Data Source=data/mmo.db")),
            ResolveMigrationsPath(ReadString("MMO_MIGRATIONS_PATH", "db/sqlite")),
            ReadInt("MMO_WORLD_WIDTH_TILES", defaultWorldWidth),
            ReadInt("MMO_WORLD_HEIGHT_TILES", defaultWorldHeight),
            // Base walk cadence. Default 250ms (the "0.6x" / 4.0-tiles-per-sec feel) — the speed everyone starts at;
            // the F1 Movement /speed dropdown brackets faster/slower around it. Override with MMO_STEP_COOLDOWN_MS.
            ReadInt("MMO_STEP_COOLDOWN_MS", 250),
            ReadInt("MMO_PERSISTENCE_CHECKPOINT_SECONDS", 15),
            // AOI radius default 30 → 18 (user decision, 2026-07-02): 30 was "insanely big" — at crowd density the
            // interest AREA (πr²) is the multiplier on AOI-gather + snapshot cost, and 18 shrinks it ~2.8×. Still
            // comfortably beyond a screen at the default camera. Live-tunable via aoi.interestRadius (F1) anytime.
            ReadFloat("MMO_INTEREST_RADIUS", 18f),
            ReadInt("MMO_MAX_VISIBLE_ENTITIES", 150),
            // AUTHORED-MAP M3 (D4): default Authored — wake on the map's `S` plaza anchors. Explicit
            // env values (distributed/clustered/scattered) keep overriding for stress/dev; on a
            // procedural (genVersion 1) map Authored falls back to the historical Distributed grid.
            ReadSpawnDistribution("MMO_SPAWN_DISTRIBUTION", SpawnDistribution.Authored),
            ReadSet("MMO_ADMIN_NAMES", "Admin"))
        {
            GenVersion = genVersion,
            MapSeed = ReadInt("MMO_MAP_SEED", 0),
            // ADUE P2: the demo's auto-pair + solo-start-guard. Default OFF (dev/headless behaviour unchanged); the
            // duo launch script sets MMO_DEMO_MODE=1.
            DemoMode = ReadBool("MMO_DEMO_MODE", false),
            DebugMovement = ReadBool("MMO_DEBUG_MOVEMENT", false),
            DebugMovementWatchNames = ReadSet("MMO_DEBUG_MOVEMENT_WATCH", ""),
            DebugMovementHitchThresholdMultiplier = ReadDouble("MMO_DEBUG_MOVEMENT_HITCH_MULTIPLIER", 1.5d),
            DebugMovementTickDurationThresholdMs = ReadDouble("MMO_DEBUG_MOVEMENT_TICK_DURATION_MS", 15d)
        };

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

        // AUTHORED-MAP M3: the shared generator would throw the same complaint at zone creation, but
        // failing here names the env vars the operator actually holds.
        if (GenVersion != 1 && GenVersion != TerrainGenerator.AuthoredGenVersion)
        {
            throw new InvalidOperationException(
                $"MMO_GEN_VERSION must be 1 (procedural) or {TerrainGenerator.AuthoredGenVersion} (authored).");
        }

        if (GenVersion == TerrainGenerator.AuthoredGenVersion
            && (WorldWidthTiles != AuthoredMaps.TownAndFloor1Width || WorldHeightTiles != AuthoredMaps.TownAndFloor1Height))
        {
            throw new InvalidOperationException(
                $"MMO_WORLD_WIDTH_TILES/MMO_WORLD_HEIGHT_TILES must be {AuthoredMaps.TownAndFloor1Width}x" +
                $"{AuthoredMaps.TownAndFloor1Height} under genVersion {TerrainGenerator.AuthoredGenVersion} " +
                "(the authored map's intrinsic dims) — unset them, or set MMO_GEN_VERSION=1 for a procedural world.");
        }

        if (StepCooldownMs < 50 || StepCooldownMs > 5000)
        {
            throw new InvalidOperationException("MMO_STEP_COOLDOWN_MS must be between 50 and 5000.");
        }

        if (PersistenceCheckpointSeconds < 1 || PersistenceCheckpointSeconds > 3600)
        {
            throw new InvalidOperationException("MMO_PERSISTENCE_CHECKPOINT_SECONDS must be between 1 and 3600.");
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

        if (DebugMovementHitchThresholdMultiplier < 1d || DebugMovementHitchThresholdMultiplier > 10d)
        {
            throw new InvalidOperationException("MMO_DEBUG_MOVEMENT_HITCH_MULTIPLIER must be between 1 and 10.");
        }

        if (DebugMovementTickDurationThresholdMs < 1d || DebugMovementTickDurationThresholdMs > 1000d)
        {
            throw new InvalidOperationException("MMO_DEBUG_MOVEMENT_TICK_DURATION_MS must be between 1 and 1000.");
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

    private static double ReadDouble(string key, double fallback)
    {
        var value = Environment.GetEnvironmentVariable(key);
        return double.TryParse(value, out var parsed) ? parsed : fallback;
    }

    private static bool ReadBool(string key, bool fallback)
    {
        var value = Environment.GetEnvironmentVariable(key);
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "on" => true,
            "0" or "false" or "no" or "off" => false,
            _ => fallback
        };
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

        var normalized = value.Trim().ToLowerInvariant();
        SpawnDistribution? parsed = normalized switch
        {
            "distributed" => SpawnDistribution.Distributed,
            "clustered" or "cluster" => SpawnDistribution.Clustered,
            "scattered" or "scatter" => SpawnDistribution.Scattered,
            "authored" => SpawnDistribution.Authored,
            _ => null
        };

        if (parsed is null)
        {
            // M3-REVIEW-FOLLOWUPS item 5 (nit): an unrecognized value silently fell back here — a typo (e.g.
            // "distirbuted") would move a stress run's 120 bots from the intended spread to whatever
            // `fallback` (today Authored — the 6 plaza tiles) ends up meaning, with no signal anything went
            // wrong. Log it loudly so a bad env var is visible at boot instead of only showing up as "why
            // are they all stacked on the plaza".
            Log.Warn($"{key}='{value}' is not a recognized spawn distribution; falling back to {fallback}.");
        }

        return parsed ?? fallback;
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
