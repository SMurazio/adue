using Mmo.Server.Configuration;
using Mmo.Server.Runtime;
using Xunit;

namespace Mmo.Server.Tests;

// S60 unit coverage for the live-tuning holder + registry: the holder seeds from ServerOptions, and the
// registry clamps known keys to the startup bounds and rejects unknown/invalid keys. The end-to-end admin
// gating + live effect is covered by AdminTuningIntegrationTests.
public sealed class ServerTuningTests
{
    private static ServerOptions Options(int stepCooldownMs = 140, float interestRadius = 35f) =>
        new(
            7777,
            20,
            "tuning-test",
            DatabaseProvider.Sqlite,
            "Data Source=:memory:",
            "db/sqlite",
            64,
            64,
            stepCooldownMs,
            15,
            interestRadius,
            150,
            SpawnDistribution.Distributed,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    [Fact]
    public void SeedsFromOptions()
    {
        var tuning = new ServerTuning(Options(stepCooldownMs: 200, interestRadius: 40f));

        Assert.Equal(200, tuning.StepCooldownMs);
        Assert.Equal(40f, tuning.InterestRadius);
    }

    [Fact]
    public void StepCooldownTicksMatchesOptionsDerivation()
    {
        var options = Options(stepCooldownMs: 140);
        var tuning = new ServerTuning(options);

        Assert.Equal(options.StepCooldownTicks, tuning.StepCooldownTicks);
    }

    [Fact]
    public void AppliesStepCooldownAndClampsToBounds()
    {
        var tuning = new ServerTuning(Options());

        Assert.True(ServerTuningRegistry.TryApply(tuning, ServerTuningRegistry.StepCooldownMsKey, 250d, out var applied));
        Assert.Equal(250, tuning.StepCooldownMs);
        Assert.Equal(250d, applied);

        // Below the [50, 5000] floor clamps to 50; above the ceiling clamps to 5000.
        Assert.True(ServerTuningRegistry.TryApply(tuning, ServerTuningRegistry.StepCooldownMsKey, 10d, out var low));
        Assert.Equal(50, tuning.StepCooldownMs);
        Assert.Equal(50d, low);

        Assert.True(ServerTuningRegistry.TryApply(tuning, ServerTuningRegistry.StepCooldownMsKey, 99999d, out var high));
        Assert.Equal(5000, tuning.StepCooldownMs);
        Assert.Equal(5000d, high);
    }

    [Fact]
    public void AppliesInterestRadiusAndClampsToBounds()
    {
        var tuning = new ServerTuning(Options());

        Assert.True(ServerTuningRegistry.TryApply(tuning, ServerTuningRegistry.InterestRadiusKey, 60d, out var applied));
        Assert.Equal(60f, tuning.InterestRadius);
        Assert.Equal(60d, applied);

        Assert.True(ServerTuningRegistry.TryApply(tuning, ServerTuningRegistry.InterestRadiusKey, 0d, out _));
        Assert.True(tuning.InterestRadius >= 1f);

        Assert.True(ServerTuningRegistry.TryApply(tuning, ServerTuningRegistry.InterestRadiusKey, 100000d, out _));
        Assert.True(tuning.InterestRadius <= 512f);
    }

    [Fact]
    public void UnknownKeyIsRejectedAndChangesNothing()
    {
        var tuning = new ServerTuning(Options(stepCooldownMs: 140, interestRadius: 35f));

        Assert.False(ServerTuningRegistry.TryApply(tuning, "does.not.exist", 999d, out _));
        Assert.Equal(140, tuning.StepCooldownMs);
        Assert.Equal(35f, tuning.InterestRadius);
    }

    [Fact]
    public void NonFiniteValueIsRejected()
    {
        var tuning = new ServerTuning(Options());

        Assert.False(ServerTuningRegistry.TryApply(tuning, ServerTuningRegistry.StepCooldownMsKey, double.NaN, out _));
        Assert.False(ServerTuningRegistry.TryApply(tuning, ServerTuningRegistry.InterestRadiusKey, double.PositiveInfinity, out _));
    }
}
