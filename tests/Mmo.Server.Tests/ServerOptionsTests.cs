using Mmo.Server.Configuration;
using Mmo.Shared.Domain;
using Xunit;

namespace Mmo.Server.Tests;

public sealed class ServerOptionsTests
{
    [Fact]
    public void FromEnvironmentReadsTileMovementOptions()
    {
        using var _ = new EnvironmentScope(new Dictionary<string, string?>
        {
            // Custom world dims require the procedural map (the authored default only boots at 384x384).
            ["MMO_GEN_VERSION"] = "1",
            ["MMO_WORLD_WIDTH_TILES"] = "96",
            ["MMO_WORLD_HEIGHT_TILES"] = "80",
            ["MMO_STEP_COOLDOWN_MS"] = "250",
            ["MMO_PERSISTENCE_CHECKPOINT_SECONDS"] = "30",
            ["MMO_SPAWN_DISTRIBUTION"] = "clustered",
            ["MMO_DEBUG_MOVEMENT"] = "true",
            ["MMO_DEBUG_MOVEMENT_WATCH"] = "Alice,Bob",
            ["MMO_DEBUG_MOVEMENT_HITCH_MULTIPLIER"] = "2.25",
            ["MMO_DEBUG_MOVEMENT_TICK_DURATION_MS"] = "12.5"
        });

        var options = ServerOptions.FromEnvironment();

        Assert.Equal(1, options.GenVersion);
        Assert.Equal(96, options.WorldWidthTiles);
        Assert.Equal(80, options.WorldHeightTiles);
        Assert.Equal(250, options.StepCooldownMs);
        Assert.Equal(TimeSpan.FromMilliseconds(250), options.StepCooldown);
        Assert.Equal(30, options.PersistenceCheckpointSeconds);
        Assert.Equal(TimeSpan.FromSeconds(30), options.PersistenceCheckpointInterval);
        Assert.Equal(SpawnDistribution.Clustered, options.SpawnDistribution);
        Assert.True(options.DebugMovement);
        Assert.Contains("Alice", options.DebugMovementWatchNames);
        Assert.Contains("Bob", options.DebugMovementWatchNames);
        Assert.Equal(2.25d, options.DebugMovementHitchThresholdMultiplier);
        Assert.Equal(12.5d, options.DebugMovementTickDurationThresholdMs);
    }

    [Fact]
    public void FromEnvironmentReadsScatteredSpawnDistribution()
    {
        using var _ = new EnvironmentScope(new Dictionary<string, string?>
        {
            ["MMO_SPAWN_DISTRIBUTION"] = "scattered"
        });

        var options = ServerOptions.FromEnvironment();

        Assert.Equal(SpawnDistribution.Scattered, options.SpawnDistribution);
    }

    [Fact]
    public void FromEnvironmentUsesProductionSizedDefaults()
    {
        using var _ = new EnvironmentScope(new Dictionary<string, string?>());

        var options = ServerOptions.FromEnvironment();

        Assert.Equal(18f, options.InterestRadius); // 30 → 18 (user decision 2026-07-02: πr² is the AOI cost multiplier; ~2.8× smaller)
        Assert.Equal(250, options.StepCooldownMs); // default base walk cadence (the 0.6x/4.0-tiles-per-sec feel)
        Assert.Equal(15, options.PersistenceCheckpointSeconds);
        // AUTHORED-MAP M3: the boot default is the authored town+floor-1 map — genVersion 2 with the
        // world-size defaults derived FROM the authored grid — waking players on the plaza anchors.
        Assert.Equal(TerrainGenerator.AuthoredGenVersion, options.GenVersion);
        Assert.Equal(AuthoredMaps.TownAndFloor1Width, options.WorldWidthTiles);
        Assert.Equal(AuthoredMaps.TownAndFloor1Height, options.WorldHeightTiles);
        Assert.Equal(SpawnDistribution.Authored, options.SpawnDistribution);
        Assert.False(options.DebugMovement);
        Assert.Empty(options.DebugMovementWatchNames);
        Assert.Equal(1.5d, options.DebugMovementHitchThresholdMultiplier);
        Assert.Equal(15d, options.DebugMovementTickDurationThresholdMs);
    }

    [Fact]
    public void FromEnvironmentGenVersion1RestoresProceduralDefaults()
    {
        // The escape hatch: MMO_GEN_VERSION=1 alone must reproduce the historical procedural world
        // (128x128 defaults). The spawn-distribution default stays Authored — on a procedural map it
        // falls back to the Distributed grid inside Zone, so behavior is unchanged.
        using var _ = new EnvironmentScope(new Dictionary<string, string?>
        {
            ["MMO_GEN_VERSION"] = "1"
        });

        var options = ServerOptions.FromEnvironment();

        Assert.Equal(1, options.GenVersion);
        Assert.Equal(128, options.WorldWidthTiles);
        Assert.Equal(128, options.WorldHeightTiles);
    }

    [Fact]
    public void FromEnvironmentRejectsWorldSizeMismatchingTheAuthoredMap()
    {
        // genVersion 2 only boots at the authored grid's intrinsic dims; a stale custom size must
        // fail loudly at options time (naming the env vars), not at zone generation.
        using var _ = new EnvironmentScope(new Dictionary<string, string?>
        {
            ["MMO_WORLD_WIDTH_TILES"] = "128",
            ["MMO_WORLD_HEIGHT_TILES"] = "128"
        });

        var exception = Assert.Throws<InvalidOperationException>(ServerOptions.FromEnvironment);

        Assert.Contains("MMO_WORLD_WIDTH_TILES", exception.Message);
        Assert.Contains("MMO_GEN_VERSION", exception.Message);
    }

    [Fact]
    public void FromEnvironmentRejectsUnknownGenVersion()
    {
        using var _ = new EnvironmentScope(new Dictionary<string, string?>
        {
            ["MMO_GEN_VERSION"] = "3"
        });

        var exception = Assert.Throws<InvalidOperationException>(ServerOptions.FromEnvironment);

        Assert.Contains("MMO_GEN_VERSION", exception.Message);
    }

    [Fact]
    public void FromEnvironmentRejectsInvalidWorldWidth()
    {
        using var _ = new EnvironmentScope(new Dictionary<string, string?>
        {
            ["MMO_WORLD_WIDTH_TILES"] = "8"
        });

        var exception = Assert.Throws<InvalidOperationException>(ServerOptions.FromEnvironment);

        Assert.Contains("MMO_WORLD_WIDTH_TILES", exception.Message);
    }

    [Fact]
    public void FromEnvironmentRejectsInvalidStepCooldown()
    {
        using var _ = new EnvironmentScope(new Dictionary<string, string?>
        {
            ["MMO_STEP_COOLDOWN_MS"] = "10"
        });

        var exception = Assert.Throws<InvalidOperationException>(ServerOptions.FromEnvironment);

        Assert.Contains("MMO_STEP_COOLDOWN_MS", exception.Message);
    }

    [Fact]
    public void FromEnvironmentRejectsInvalidPersistenceCheckpoint()
    {
        using var _ = new EnvironmentScope(new Dictionary<string, string?>
        {
            ["MMO_PERSISTENCE_CHECKPOINT_SECONDS"] = "0"
        });

        var exception = Assert.Throws<InvalidOperationException>(ServerOptions.FromEnvironment);

        Assert.Contains("MMO_PERSISTENCE_CHECKPOINT_SECONDS", exception.Message);
    }

    private sealed class EnvironmentScope : IDisposable
    {
        private static readonly string[] Keys =
        [
            "MMO_PORT",
            "MMO_TICK_RATE",
            "MMO_CONNECTION_KEY",
            "MMO_DB_PROVIDER",
            "MMO_DB",
            "MMO_MIGRATIONS_PATH",
            "MMO_GEN_VERSION",
            "MMO_WORLD_WIDTH_TILES",
            "MMO_WORLD_HEIGHT_TILES",
            "MMO_STEP_COOLDOWN_MS",
            "MMO_PERSISTENCE_CHECKPOINT_SECONDS",
            "MMO_INTEREST_RADIUS",
            "MMO_MAX_VISIBLE_ENTITIES",
            "MMO_SPAWN_DISTRIBUTION",
            "MMO_ADMIN_NAMES",
            "MMO_DEBUG_MOVEMENT",
            "MMO_DEBUG_MOVEMENT_WATCH",
            "MMO_DEBUG_MOVEMENT_HITCH_MULTIPLIER",
            "MMO_DEBUG_MOVEMENT_TICK_DURATION_MS"
        ];

        private readonly Dictionary<string, string?> _original = new(StringComparer.Ordinal);

        public EnvironmentScope(IReadOnlyDictionary<string, string?> overrides)
        {
            foreach (var key in Keys)
            {
                _original[key] = Environment.GetEnvironmentVariable(key);
                Environment.SetEnvironmentVariable(key, null);
            }

            foreach (var (key, value) in overrides)
            {
                Environment.SetEnvironmentVariable(key, value);
            }
        }

        public void Dispose()
        {
            foreach (var (key, value) in _original)
            {
                Environment.SetEnvironmentVariable(key, value);
            }
        }
    }
}
