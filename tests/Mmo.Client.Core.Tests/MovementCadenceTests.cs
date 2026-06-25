using Xunit;

namespace Mmo.Client.Core.Tests;

// The effective step cadence is the server-tick-quantized step cooldown — the client mirrors the server's
// derivation so the two stay in cadence lockstep. (Preserved from the retired TileInterpolatorTests when the
// tile interpolator was deleted in the Phase 5 continuous migration; the cadence quantization is still live —
// it drives ResolveCadence / the predictor speed derivation.)
public sealed class MovementCadenceTests
{
    [Fact]
    public void EffectiveCadenceMatchesServerTickQuantization()
    {
        Assert.Equal(150d, MovementCadence.EffectiveStepCadenceMs(140, 20));
        Assert.Equal(200d, MovementCadence.EffectiveStepCadenceMs(200, 20));
    }
}
