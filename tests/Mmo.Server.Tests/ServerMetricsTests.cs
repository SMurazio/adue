using Mmo.Server.Runtime;
using Xunit;

namespace Mmo.Server.Tests;

public sealed class ServerMetricsTests
{
    [Fact]
    public void RecordTickCapturesBudgetCategoriesAndScheduleDrift()
    {
        var metrics = new ServerMetrics();
        var budget = new TickBudgetSample(1, 2, 3, 4, 5, 6);

        metrics.RecordTick(TimeSpan.FromMilliseconds(25), TimeSpan.FromMilliseconds(7), budget);

        var snapshot = metrics.Capture();
        Assert.Equal(25, snapshot.TickLastMs);
        Assert.Equal(7, snapshot.TickScheduleDriftAverageMs);
        Assert.Equal(7, snapshot.TickScheduleDriftMaxMs);
        Assert.Equal(budget, snapshot.TickBudgetAverageMs);
        Assert.Equal(budget, snapshot.TickBudgetMaxMs);

        var window = metrics.CaptureWindow(TimeSpan.FromSeconds(5));
        Assert.Equal(7, window.TickScheduleDriftAverageMs);
        Assert.Equal(budget, window.TickBudgetAverageMs);
    }

    [Fact]
    public void RecordSnapshotSentCapturesPerClientSnapshotBytes()
    {
        var metrics = new ServerMetrics();

        metrics.RecordSnapshotSent(100, 3, 5);
        metrics.RecordSnapshotSent(300, 3, 5);

        var snapshot = metrics.Capture();
        Assert.Equal(200, snapshot.SnapshotClientBytesAverage);
        Assert.Equal(300, snapshot.SnapshotClientBytesMax);
        Assert.Equal(2, snapshot.SnapshotCulled);
    }
}
