using Mmo.Server.Runtime;
using Xunit;

namespace Mmo.Server.Tests;

public sealed class ServerRuntimeGuardTests
{
    [Fact]
    public void TryRunRecordsRuntimeFaultAndSuppressesException()
    {
        var metrics = new ServerMetrics();
        var guard = new ServerRuntimeGuard(metrics);

        var completed = guard.TryRun("test", () => throw new InvalidOperationException("boom"));

        Assert.False(completed);
        Assert.Equal(1, metrics.Capture().RuntimeFaults);
        Assert.Equal(1, metrics.CaptureWindow(TimeSpan.FromSeconds(5)).RuntimeFaults);
    }

    [Fact]
    public void TryRunDoesNotRecordRuntimeFaultOnSuccess()
    {
        var metrics = new ServerMetrics();
        var guard = new ServerRuntimeGuard(metrics);

        var completed = guard.TryRun("test", () => { });

        Assert.True(completed);
        Assert.Equal(0, metrics.Capture().RuntimeFaults);
    }
}
