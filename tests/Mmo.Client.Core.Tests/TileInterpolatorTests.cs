using Mmo.Shared.Domain;
using Xunit;

namespace Mmo.Client.Core.Tests;

public sealed class TileInterpolatorTests
{
    [Fact]
    public void EffectiveCadenceMatchesServerTickQuantization()
    {
        Assert.Equal(150d, MovementCadence.EffectiveStepCadenceMs(140, 20));
        Assert.Equal(200d, MovementCadence.EffectiveStepCadenceMs(200, 20));
    }

    [Fact]
    public void LocalConfirmedStepStartsWithoutBuffer()
    {
        var interpolator = new TileInterpolator(new TileCoord(0, 0), 150, 0);

        interpolator.Confirm(new TileCoord(1, 0), TimeSpan.Zero);
        var halfway = interpolator.Sample(TimeSpan.FromMilliseconds(75));

        Assert.InRange(halfway.X, 0.49, 0.51);
        Assert.Equal(0, halfway.Y);
    }

    [Fact]
    public void RemoteJitteredStepsStayMonotonicWithoutBoundaryStall()
    {
        var interpolator = new TileInterpolator(new TileCoord(0, 0), 150, 195);
        interpolator.Confirm(new TileCoord(1, 0), TimeSpan.Zero);
        interpolator.Confirm(new TileCoord(2, 0), TimeSpan.FromMilliseconds(140));
        interpolator.Confirm(new TileCoord(3, 0), TimeSpan.FromMilliseconds(290));

        var previous = double.NegativeInfinity;
        var positions = new Dictionary<int, RenderPosition>();
        for (var ms = 0; ms <= 700; ms += 25)
        {
            var position = interpolator.Sample(TimeSpan.FromMilliseconds(ms));
            positions[ms] = position;
            Assert.True(position.X + 0.0001 >= previous, $"position moved backward at {ms}ms");
            previous = position.X;
        }

        Assert.True(positions[375].X > positions[350].X);
        Assert.True(positions[525].X > positions[500].X);
        Assert.InRange(positions[700].X, 2.8, 3.0);
    }
}
