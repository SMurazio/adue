using Mmo.Shared.Domain;
using Xunit;

namespace Mmo.Client.Core.Tests;

public sealed class ScreenRelativeDirectionMapperTests
{
    [Theory]
    [InlineData(0, -1, Direction8.NW)]
    [InlineData(1, -1, Direction8.N)]
    [InlineData(1, 0, Direction8.NE)]
    [InlineData(1, 1, Direction8.E)]
    [InlineData(0, 1, Direction8.SE)]
    [InlineData(-1, 1, Direction8.S)]
    [InlineData(-1, 0, Direction8.SW)]
    [InlineData(-1, -1, Direction8.W)]
    public void MapsScreenRelativeInputAxesToWorldDirection(int x, int y, Direction8 expected)
    {
        Assert.Equal(expected, ScreenRelativeDirectionMapper.FromInputAxes(x, y));
    }

    [Fact]
    public void NoHeldInputReturnsNull()
    {
        Assert.Null(ScreenRelativeDirectionMapper.FromInputAxes(0, 0));
    }
}
