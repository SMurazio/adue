using System.Linq;
using Mmo.Client.Core;
using Xunit;

namespace Mmo.Client.Core.Tests;

// S106 — the F6 "Move speed" dropdown's multiplier -> cadence -> label math (MovementSpeedOptions). Pure +
// deterministic, so it unit-tests the option set, the 1.0x-at-base-walk pivot, the clamp, and the numbers-only
// labelling directly.
public sealed class MovementSpeedOptionsTests
{
    private const int DefaultBaseStepMs = 150; // ServerOptions default (SPEED1: pinned constant 150 ms = 3 ticks).
    private const int DefaultTickRate = 20;    // 50 ms tick interval.

    [Fact]
    public void DefaultWalkIsExactlyOneTimes()
    {
        var options = MovementSpeedOptions.Build(DefaultBaseStepMs, DefaultTickRate);

        // baseWalkTicks = ceil(150 / 50) = 3, so N=3 is the 1.0x default walk.
        var walk = options.Single(o => o.IsDefaultWalk);
        Assert.Equal(3, walk.Ticks);
        Assert.Equal(1.0d, walk.Multiplier, 9);
        Assert.Equal(150d, walk.CadenceMs, 9); // 3 ticks * 50 ms.
        Assert.Equal(1000d / 150d, walk.UnitsPerSecond, 9);
    }

    [Fact]
    public void OptionsAreTickQuantizedFastestFirstWithCorrectMultiplierAndCadence()
    {
        var options = MovementSpeedOptions.Build(DefaultBaseStepMs, DefaultTickRate);

        // Fastest first, strictly increasing tick count (so strictly decreasing speed).
        for (var i = 1; i < options.Count; i++)
        {
            Assert.True(options[i].Ticks > options[i - 1].Ticks);
            Assert.True(options[i].CadenceMs > options[i - 1].CadenceMs);
            Assert.True(options[i].Multiplier < options[i - 1].Multiplier);
        }

        // Every option's cadence is its tick count * the tick interval, multiplier is baseWalkTicks/N, and
        // units/s is the inverse of the cadence in seconds.
        const double tickIntervalMs = 1000d / DefaultTickRate;
        const int baseWalkTicks = 3;
        foreach (var option in options)
        {
            Assert.Equal(option.Ticks * tickIntervalMs, option.CadenceMs, 9);
            Assert.Equal((double)baseWalkTicks / option.Ticks, option.Multiplier, 9);
            Assert.Equal(1000d / option.CadenceMs, option.UnitsPerSecond, 9);
        }
    }

    [Fact]
    public void EveryOptionStaysWithinServerEffectiveCooldownClamp()
    {
        var options = MovementSpeedOptions.Build(DefaultBaseStepMs, DefaultTickRate);

        // The server clamps the effective cooldown to [50, 5000] ms (GameServer.Min/MaxEffectiveStepCooldownMs),
        // so no offered cadence may fall outside it — picking such a speed would be silently clamped server-side to
        // a DIFFERENT cadence than the label promises.
        Assert.NotEmpty(options);
        foreach (var option in options)
        {
            Assert.InRange(option.CadenceMs,
                MovementSpeedOptions.MinEffectiveStepCooldownMs,
                MovementSpeedOptions.MaxEffectiveStepCooldownMs);
        }
    }

    [Fact]
    public void LabelIsNumbersOnlyNoBracketName()
    {
        // 1.50x - 100 ms - 10.0/s (no "Walk"/"Run").
        var label = MovementSpeedOptions.FormatLabel(1.5d, 100d, 10.0d);
        Assert.Equal("1.50x - 100 ms - 10.0/s", label);
    }

    [Fact]
    public void SpeedCommandArgumentRoundTripsInvariantly()
    {
        // 3/8 = 0.375 must format with a '.' decimal so the server's invariant double.TryParse reads it exactly.
        var arg = MovementSpeedOptions.FormatSpeedCommandArgument(3d / 8d);
        Assert.Equal("0.375", arg);
        Assert.Equal(0.375d, double.Parse(arg, System.Globalization.CultureInfo.InvariantCulture), 9);
    }
}
