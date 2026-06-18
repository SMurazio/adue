using Mmo.Client.Web;
using Mmo.Shared.Domain;
using Xunit;

namespace Mmo.Server.Tests;

public sealed class WebBridgeSessionTests
{
    [Theory]
    [InlineData("N", Direction8.N)]
    [InlineData("NE", Direction8.NE)]
    [InlineData("E", Direction8.E)]
    [InlineData("SE", Direction8.SE)]
    [InlineData("S", Direction8.S)]
    [InlineData("SW", Direction8.SW)]
    [InlineData("W", Direction8.W)]
    [InlineData("NW", Direction8.NW)]
    public void TryParseDirectionAcceptsDirectionNames(string input, Direction8 expected)
    {
        Assert.True(WebBridgeSession.TryParseDirection(input, out var parsed));
        Assert.Equal(expected, parsed);
    }

    [Theory]
    [InlineData("up")]
    [InlineData("down")]
    [InlineData("left")]
    [InlineData("right")]
    public void TryParseDirectionRejectsLegacyDirectionWords(string input)
    {
        Assert.False(WebBridgeSession.TryParseDirection(input, out _));
    }
}
