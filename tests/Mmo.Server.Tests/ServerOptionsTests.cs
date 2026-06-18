using Mmo.Server.Configuration;
using Mmo.Shared.Domain;
using Xunit;

namespace Mmo.Server.Tests;

public sealed class ServerOptionsTests
{
    [Fact]
    public void FromEnvironmentReadsWorldBounds()
    {
        using var _ = new EnvironmentScope(new Dictionary<string, string?>
        {
            ["MMO_WORLD_MIN_X"] = "-100",
            ["MMO_WORLD_MAX_X"] = "101",
            ["MMO_WORLD_MIN_Y"] = "-50",
            ["MMO_WORLD_MAX_Y"] = "51"
        });

        var options = ServerOptions.FromEnvironment();

        Assert.Equal(new WorldBounds(-100, 101, -50, 51), options.WorldBounds);
    }

    [Fact]
    public void FromEnvironmentRejectsWorldBoundsOutsideSnapshotRange()
    {
        using var _ = new EnvironmentScope(new Dictionary<string, string?>
        {
            ["MMO_WORLD_MAX_X"] = "4000"
        });

        var exception = Assert.Throws<InvalidOperationException>(ServerOptions.FromEnvironment);

        Assert.Contains("snapshot range", exception.Message);
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
            "MMO_MOVE_SPEED",
            "MMO_INTEREST_RADIUS",
            "MMO_MAX_VISIBLE_ENTITIES",
            "MMO_WORLD_MIN_X",
            "MMO_WORLD_MAX_X",
            "MMO_WORLD_MIN_Y",
            "MMO_WORLD_MAX_Y",
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
