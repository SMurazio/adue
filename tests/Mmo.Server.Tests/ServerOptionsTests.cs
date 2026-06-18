using Mmo.Server.Configuration;
using Xunit;

namespace Mmo.Server.Tests;

public sealed class ServerOptionsTests
{
    [Fact]
    public void FromEnvironmentReadsTileMovementOptions()
    {
        using var _ = new EnvironmentScope(new Dictionary<string, string?>
        {
            ["MMO_WORLD_WIDTH_TILES"] = "96",
            ["MMO_WORLD_HEIGHT_TILES"] = "80",
            ["MMO_STEP_COOLDOWN_MS"] = "250",
            ["MMO_SPAWN_DISTRIBUTION"] = "clustered"
        });

        var options = ServerOptions.FromEnvironment();

        Assert.Equal(96, options.WorldWidthTiles);
        Assert.Equal(80, options.WorldHeightTiles);
        Assert.Equal(250, options.StepCooldownMs);
        Assert.Equal(TimeSpan.FromMilliseconds(250), options.StepCooldown);
        Assert.Equal(SpawnDistribution.Clustered, options.SpawnDistribution);
    }

    [Fact]
    public void FromEnvironmentUsesProductionSizedDefaults()
    {
        using var _ = new EnvironmentScope(new Dictionary<string, string?>());

        var options = ServerOptions.FromEnvironment();

        Assert.Equal(40f, options.InterestRadius);
        Assert.Equal(140, options.StepCooldownMs);
        Assert.Equal(128, options.WorldWidthTiles);
        Assert.Equal(128, options.WorldHeightTiles);
        Assert.Equal(SpawnDistribution.Distributed, options.SpawnDistribution);
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
            "MMO_WORLD_WIDTH_TILES",
            "MMO_WORLD_HEIGHT_TILES",
            "MMO_STEP_COOLDOWN_MS",
            "MMO_INTEREST_RADIUS",
            "MMO_MAX_VISIBLE_ENTITIES",
            "MMO_SPAWN_DISTRIBUTION",
            "MMO_ADMIN_NAMES"
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
