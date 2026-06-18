using System.Diagnostics;
using Mmo.Server.Runtime;
using Xunit;

namespace Mmo.Server.Tests;

public sealed class TickBudgetRecorderTests
{
    [Fact]
    public void RecordElapsedAddsMovementBudget()
    {
        var recorder = new TickBudgetRecorder();

        recorder.RecordElapsed(TickBudgetCategory.Movement, Stopwatch.Frequency / 1000);

        var sample = recorder.ToSample();
        Assert.True(sample.MovementMs > 0);
        Assert.Equal(0, sample.AoiMs);
        Assert.Equal(0, sample.SerializeMs);
        Assert.Equal(0, sample.NetworkMs);
        Assert.Equal(0, sample.PersistenceMs);
        Assert.Equal(0, sample.OtherMs);
    }
}
