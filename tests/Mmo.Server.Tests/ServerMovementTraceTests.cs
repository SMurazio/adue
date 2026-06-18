using Mmo.Server.Configuration;
using Mmo.Server.Runtime;
using Xunit;

namespace Mmo.Server.Tests;

public sealed class ServerMovementTraceTests
{
    [Fact]
    public void TickHitchTraceEmitsBudgetAndGcContextWhenEnabled()
    {
        var lines = new List<string>();
        var trace = new ServerMovementTrace(CreateOptions() with { DebugMovement = true }, lines.Add);

        trace.TickHitch(
            serverTick: 42,
            interTickGap: TimeSpan.FromMilliseconds(90),
            tickDuration: TimeSpan.FromMilliseconds(2),
            scheduleDrift: TimeSpan.FromMilliseconds(40),
            budget: new TickBudgetSample(1, 2, 3, 4, 5, 6),
            catchUpTicks: 2,
            gen0Delta: 1,
            gen1Delta: 0,
            gen2Delta: 0,
            tickInterval: TimeSpan.FromMilliseconds(50));

        var line = Assert.Single(lines);
        Assert.Contains("event=tick_hitch", line);
        Assert.Contains("interMs=90", line);
        Assert.Contains("driftMs=40", line);
        Assert.Contains("catchUpTicks=2", line);
        Assert.Contains("gc0=1", line);
        Assert.Contains("moveMs=1", line);
        Assert.Contains("aoiMs=2", line);
        Assert.Contains("serMs=3", line);
        Assert.Contains("netMs=4", line);
        Assert.Contains("persistMs=5", line);
        Assert.Contains("otherMs=6", line);
    }

    [Fact]
    public void TickHitchTraceIsSilentWhenDisabled()
    {
        var lines = new List<string>();
        var trace = new ServerMovementTrace(CreateOptions(), lines.Add);

        trace.TickHitch(
            42,
            TimeSpan.FromMilliseconds(90),
            TimeSpan.FromMilliseconds(2),
            TimeSpan.FromMilliseconds(40),
            TickBudgetSample.Zero,
            2,
            0,
            0,
            0,
            TimeSpan.FromMilliseconds(50));

        Assert.Empty(lines);
    }

    private static ServerOptions CreateOptions()
    {
        return new ServerOptions(
            7777,
            20,
            "trace-test",
            DatabaseProvider.Sqlite,
            "Data Source=:memory:",
            "db/sqlite",
            64,
            64,
            140,
            15,
            40,
            150,
            SpawnDistribution.Clustered,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    }
}
