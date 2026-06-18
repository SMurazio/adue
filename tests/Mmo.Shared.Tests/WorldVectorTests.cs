using Mmo.Shared.Domain;
using Xunit;

namespace Mmo.Shared.Tests;

public sealed class WorldVectorTests
{
    [Fact]
    public void NormalizeOrZeroReturnsUnitVector()
    {
        var normalized = new WorldVector(3, 4).NormalizeOrZero();

        Assert.Equal(0.6f, normalized.X, precision: 3);
        Assert.Equal(0.8f, normalized.Y, precision: 3);
    }

    [Fact]
    public void NormalizeOrZeroReturnsZeroForTinyVector()
    {
        var normalized = new WorldVector(0.00001f, 0.00001f).NormalizeOrZero();

        Assert.Equal(WorldVector.Zero, normalized);
    }
}
