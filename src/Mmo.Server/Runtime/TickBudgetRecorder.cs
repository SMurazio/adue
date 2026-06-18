using System.Diagnostics;

namespace Mmo.Server.Runtime;

internal sealed class TickBudgetRecorder
{
    private readonly long[] _elapsedTicks = new long[TickBudgetCategoryCount];

    public const int TickBudgetCategoryCount = 6;

    public void Reset()
    {
        Array.Clear(_elapsedTicks);
    }

    public TickBudgetScope Measure(TickBudgetCategory category)
    {
        return new TickBudgetScope(this, category);
    }

    public void RecordElapsed(TickBudgetCategory category, long elapsedTicks)
    {
        Add(category, elapsedTicks);
    }

    public TickBudgetSample ToSample()
    {
        return new TickBudgetSample(
            ToMilliseconds(_elapsedTicks[(int)TickBudgetCategory.Movement]),
            ToMilliseconds(_elapsedTicks[(int)TickBudgetCategory.Aoi]),
            ToMilliseconds(_elapsedTicks[(int)TickBudgetCategory.Serialize]),
            ToMilliseconds(_elapsedTicks[(int)TickBudgetCategory.Network]),
            ToMilliseconds(_elapsedTicks[(int)TickBudgetCategory.Persistence]),
            ToMilliseconds(_elapsedTicks[(int)TickBudgetCategory.Other]));
    }

    private void Add(TickBudgetCategory category, long elapsedTicks)
    {
        var index = (int)category;
        if ((uint)index < _elapsedTicks.Length)
        {
            _elapsedTicks[index] += elapsedTicks;
        }
    }

    private static double ToMilliseconds(long elapsedTicks)
    {
        return elapsedTicks * 1000d / Stopwatch.Frequency;
    }

    internal readonly struct TickBudgetScope : IDisposable
    {
        private readonly TickBudgetRecorder _recorder;
        private readonly TickBudgetCategory _category;
        private readonly long _startedAt;

        public TickBudgetScope(TickBudgetRecorder recorder, TickBudgetCategory category)
        {
            _recorder = recorder;
            _category = category;
            _startedAt = Stopwatch.GetTimestamp();
        }

        public void Dispose()
        {
            _recorder.Add(_category, Stopwatch.GetTimestamp() - _startedAt);
        }
    }
}
