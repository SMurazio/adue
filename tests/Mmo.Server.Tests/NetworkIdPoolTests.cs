using Mmo.Server.Runtime;
using Xunit;

namespace Mmo.Server.Tests;

public sealed class NetworkIdPoolTests
{
    [Fact]
    public void RentReusesReturnedNetworkId()
    {
        var pool = new NetworkIdPool();
        var first = pool.Rent();
        var second = pool.Rent();

        pool.Return(first);

        Assert.Equal(first, pool.Rent());
        Assert.NotEqual(second, pool.Rent());
    }

    [Fact]
    public void RentNeverIssuesIdOutsideSnapshotRange()
    {
        var pool = new NetworkIdPool();
        uint last = 0;

        for (var i = 0; i < ushort.MaxValue; i++)
        {
            last = pool.Rent();
        }

        Assert.Equal(ushort.MaxValue, last);
        Assert.Throws<InvalidOperationException>(() => pool.Rent());
    }
}
