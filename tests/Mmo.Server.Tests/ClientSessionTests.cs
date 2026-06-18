using Mmo.Server.Runtime;
using Mmo.Shared.Domain;
using Xunit;

namespace Mmo.Server.Tests;

public sealed class ClientSessionTests
{
    [Fact]
    public void AdvanceClampsAuthoritativePositionToWorldBounds()
    {
        var session = new ClientSession(null!);
        var bounds = new WorldBounds(-10, 10, -5, 5);
        session.Authenticate(1, Guid.NewGuid(), "Player", ClientRole.Player, "sandbox", new WorldVector(9, 4));
        session.SetDirection(new WorldVector(1, 1));

        session.Advance(1, 10, bounds);

        Assert.Equal(new WorldVector(10, 5), session.Position);
    }
}
