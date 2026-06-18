using System.Collections.Concurrent;
using Mmo.Server.Runtime;
using Xunit;

namespace Mmo.Server.Tests;

public sealed class LogTests
{
    [Fact]
    public void FlushWaitsForQueuedLogLines()
    {
        var lines = new ConcurrentQueue<string>();
        using var sink = new AsyncConsoleLogSink(lines.Enqueue, capacity: 8);

        sink.Write(LogLevel.Info, "hello");
        sink.Write(LogLevel.Warn, "careful");
        sink.Write(LogLevel.Error, "boom");

        Assert.True(sink.Flush(TimeSpan.FromSeconds(2)));
        Assert.Equal(3, lines.Count);
        Assert.Contains(lines, line => line.Contains("[info] hello", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("[warn] careful", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("[error] boom", StringComparison.Ordinal));
    }

    [Fact]
    public void NonErrorLogsAreDroppedWhenTheQueueIsFull()
    {
        var lines = new ConcurrentQueue<string>();
        using var writerEntered = new ManualResetEventSlim();
        using var releaseWriter = new ManualResetEventSlim();
        using var sink = new AsyncConsoleLogSink(line =>
        {
            writerEntered.Set();
            if (releaseWriter.Wait(TimeSpan.FromSeconds(2)))
            {
                lines.Enqueue(line);
            }
        }, capacity: 1);

        sink.Write(LogLevel.Info, "first");
        Assert.True(writerEntered.Wait(TimeSpan.FromSeconds(2)));

        sink.Write(LogLevel.Info, "dropped");
        releaseWriter.Set();

        Assert.True(sink.Flush(TimeSpan.FromSeconds(2)));
        Assert.Equal(1, sink.DroppedNonErrorCount);
        Assert.Single(lines);
        Assert.Contains("[info] first", lines.Single(), StringComparison.Ordinal);
    }

    [Fact]
    public void ErrorLogsArePreservedWhenTheQueueIsFull()
    {
        var lines = new ConcurrentQueue<string>();
        using var writerEntered = new ManualResetEventSlim();
        using var releaseWriter = new ManualResetEventSlim();
        using var sink = new AsyncConsoleLogSink(line =>
        {
            writerEntered.Set();
            if (releaseWriter.Wait(TimeSpan.FromSeconds(2)))
            {
                lines.Enqueue(line);
            }
        }, capacity: 2);

        sink.Write(LogLevel.Info, "first");
        Assert.True(writerEntered.Wait(TimeSpan.FromSeconds(2)));
        sink.Write(LogLevel.Info, "queued-info");
        sink.Write(LogLevel.Error, "must-keep");
        releaseWriter.Set();

        Assert.True(sink.Flush(TimeSpan.FromSeconds(2)));
        Assert.Equal(1, sink.DroppedNonErrorCount);
        Assert.Contains(lines, line => line.Contains("[info] first", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains("queued-info", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("[error] must-keep", StringComparison.Ordinal));
    }
}
