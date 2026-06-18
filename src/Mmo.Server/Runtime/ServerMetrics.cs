using System.Text;
using Mmo.Shared.Protocol;

namespace Mmo.Server.Runtime;

public sealed class ServerMetrics
{
    private const int MessageTypeCount = 256;
    private const int WindowBucketCount = 120;

    private readonly long[] _receivedMessages = new long[MessageTypeCount];
    private readonly long[] _sentMessages = new long[MessageTypeCount];
    private readonly long[] _receivedBytesByType = new long[MessageTypeCount];
    private readonly long[] _sentBytesByType = new long[MessageTypeCount];
    private readonly double[] _tickBudgetTotalMs = new double[TickBudgetRecorder.TickBudgetCategoryCount];
    private readonly double[] _tickBudgetMaxMs = new double[TickBudgetRecorder.TickBudgetCategoryCount];
    private readonly MetricBucket[] _buckets = new MetricBucket[WindowBucketCount];
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;

    private long _tickCount;
    private double _tickTotalMs;
    private double _tickLastMs;
    private double _tickMaxMs;
    private double _tickScheduleDriftTotalMs;
    private double _tickScheduleDriftMaxMs;
    private long _peersConnected;
    private long _peersDisconnected;
    private long _networkErrors;
    private long _badPackets;
    private long _sendFailures;
    private long _runtimeFaults;
    private long _receivedBytes;
    private long _sentBytes;
    private long _snapshotsSent;
    private long _snapshotBytes;
    private long _snapshotCulled;
    private long _snapshotVisibleEntities;
    private long _snapshotMaxVisibleEntities;
    private long _snapshotClientByteSamples;
    private long _snapshotClientBytesMax;
    private long _loginAccepted;
    private long _loginRejected;
    private double _loginTotalMs;
    private double _loginMaxMs;

    public ServerMetrics()
    {
        for (var i = 0; i < _buckets.Length; i++)
        {
            _buckets[i] = new MetricBucket();
        }
    }

    public void RecordTick(TimeSpan elapsed)
    {
        RecordTick(elapsed, TimeSpan.Zero, TickBudgetSample.Zero);
    }

    public void RecordTick(TimeSpan elapsed, TimeSpan scheduleDrift, TickBudgetSample budget)
    {
        var elapsedMs = elapsed.TotalMilliseconds;
        var driftMs = Math.Max(0, scheduleDrift.TotalMilliseconds);
        _tickCount++;
        _tickTotalMs += elapsedMs;
        _tickLastMs = elapsedMs;
        _tickMaxMs = Math.Max(_tickMaxMs, elapsedMs);
        _tickScheduleDriftTotalMs += driftMs;
        _tickScheduleDriftMaxMs = Math.Max(_tickScheduleDriftMaxMs, driftMs);
        AddTickBudget(_tickBudgetTotalMs, _tickBudgetMaxMs, budget);

        var bucket = CurrentBucket();
        bucket.TickCount++;
        bucket.TickTotalMs += elapsedMs;
        bucket.TickMaxMs = Math.Max(bucket.TickMaxMs, elapsedMs);
        bucket.TickScheduleDriftTotalMs += driftMs;
        bucket.TickScheduleDriftMaxMs = Math.Max(bucket.TickScheduleDriftMaxMs, driftMs);
        AddTickBudget(bucket.TickBudgetTotalMs, bucket.TickBudgetMaxMs, budget);
    }

    public void RecordPeerConnected()
    {
        _peersConnected++;
    }

    public void RecordPeerDisconnected()
    {
        _peersDisconnected++;
    }

    public void RecordNetworkError()
    {
        _networkErrors++;
        CurrentBucket().NetworkErrors++;
    }

    public void RecordBadPacket()
    {
        _badPackets++;
        CurrentBucket().BadPackets++;
    }

    public void RecordReceived(IProtocolMessage message, int byteCount)
    {
        _receivedBytes += byteCount;
        var bucket = CurrentBucket();
        bucket.ReceivedBytes += byteCount;
        bucket.ReceivedMessageCount++;

        var index = (int)message.Type;
        if ((uint)index < MessageTypeCount)
        {
            _receivedMessages[index]++;
            _receivedBytesByType[index] += byteCount;
            bucket.ReceivedMessages[index]++;
        }
    }

    public void RecordSent(IProtocolMessage message, int byteCount)
    {
        _sentBytes += byteCount;
        var bucket = CurrentBucket();
        bucket.SentBytes += byteCount;
        bucket.SentMessageCount++;

        var index = (int)message.Type;
        if ((uint)index < MessageTypeCount)
        {
            _sentMessages[index]++;
            _sentBytesByType[index] += byteCount;
            bucket.SentMessages[index]++;
        }
    }

    public void RecordSnapshotSent(int byteCount, int visibleEntities, int totalEntities)
    {
        _sentBytes += byteCount;
        _snapshotsSent++;
        _snapshotBytes += byteCount;
        _snapshotVisibleEntities += visibleEntities;
        _snapshotMaxVisibleEntities = Math.Max(_snapshotMaxVisibleEntities, visibleEntities);
        _snapshotClientByteSamples++;
        _snapshotClientBytesMax = Math.Max(_snapshotClientBytesMax, byteCount);

        var bucket = CurrentBucket();
        bucket.SentBytes += byteCount;
        bucket.SentMessageCount++;
        bucket.SnapshotsSent++;
        bucket.SnapshotBytes += byteCount;
        bucket.SnapshotVisibleEntities += visibleEntities;
        bucket.SnapshotMaxVisibleEntities = Math.Max(bucket.SnapshotMaxVisibleEntities, visibleEntities);
        bucket.SnapshotClientByteSamples++;
        bucket.SnapshotClientBytesMax = Math.Max(bucket.SnapshotClientBytesMax, byteCount);

        var index = (int)MessageType.WorldSnapshot;
        _sentMessages[index]++;
        _sentBytesByType[index] += byteCount;
        bucket.SentMessages[index]++;

        if (visibleEntities < totalEntities)
        {
            _snapshotCulled++;
            bucket.SnapshotCulled++;
        }
    }

    public void RecordSendFailure()
    {
        _sendFailures++;
        CurrentBucket().SendFailures++;
    }

    public void RecordRuntimeFault()
    {
        _runtimeFaults++;
        CurrentBucket().RuntimeFaults++;
    }

    public void RecordLogin(bool accepted, TimeSpan elapsed)
    {
        if (accepted)
        {
            _loginAccepted++;
        }
        else
        {
            _loginRejected++;
        }

        var elapsedMs = elapsed.TotalMilliseconds;
        _loginTotalMs += elapsedMs;
        _loginMaxMs = Math.Max(_loginMaxMs, elapsedMs);

        var bucket = CurrentBucket();
        if (accepted)
        {
            bucket.LoginAccepted++;
        }
        else
        {
            bucket.LoginRejected++;
        }

        bucket.LoginTotalMs += elapsedMs;
        bucket.LoginMaxMs = Math.Max(bucket.LoginMaxMs, elapsedMs);
    }

    public MetricsSnapshot Capture()
    {
        return new MetricsSnapshot(
            DateTimeOffset.UtcNow - _startedAt,
            _tickCount,
            _tickLastMs,
            _tickCount == 0 ? 0 : _tickTotalMs / _tickCount,
            _tickMaxMs,
            _tickCount == 0 ? 0 : _tickScheduleDriftTotalMs / _tickCount,
            _tickScheduleDriftMaxMs,
            AverageBudget(_tickBudgetTotalMs, _tickCount),
            MaxBudget(_tickBudgetMaxMs),
            _peersConnected,
            _peersDisconnected,
            _networkErrors,
            _badPackets,
            _sendFailures,
            _runtimeFaults,
            _receivedBytes,
            _sentBytes,
            _snapshotsSent,
            _snapshotBytes,
            _snapshotCulled,
            _snapshotsSent == 0 ? 0 : (double)_snapshotVisibleEntities / _snapshotsSent,
            _snapshotMaxVisibleEntities,
            _snapshotClientByteSamples == 0 ? 0 : (double)_snapshotBytes / _snapshotClientByteSamples,
            _snapshotClientBytesMax,
            _loginAccepted,
            _loginRejected,
            _loginAccepted + _loginRejected == 0 ? 0 : _loginTotalMs / (_loginAccepted + _loginRejected),
            _loginMaxMs,
            (long[])_receivedMessages.Clone(),
            (long[])_sentMessages.Clone(),
            (long[])_receivedBytesByType.Clone(),
            (long[])_sentBytesByType.Clone());
    }

    public MetricsWindowSnapshot CaptureWindow(TimeSpan window)
    {
        var now = DateTimeOffset.UtcNow;
        var nowSecond = now.ToUnixTimeSeconds();
        var windowSeconds = Math.Max(1, (int)Math.Ceiling(window.TotalSeconds));
        var oldestSecond = nowSecond - windowSeconds + 1;
        var effectiveSeconds = Math.Max(0.001, Math.Min(window.TotalSeconds, (now - _startedAt).TotalSeconds));

        var receivedMessages = new long[MessageTypeCount];
        var sentMessages = new long[MessageTypeCount];
        long tickCount = 0;
        double tickTotalMs = 0;
        double tickMaxMs = 0;
        double tickScheduleDriftTotalMs = 0;
        double tickScheduleDriftMaxMs = 0;
        var tickBudgetTotalMs = new double[TickBudgetRecorder.TickBudgetCategoryCount];
        var tickBudgetMaxMs = new double[TickBudgetRecorder.TickBudgetCategoryCount];
        long networkErrors = 0;
        long badPackets = 0;
        long sendFailures = 0;
        long runtimeFaults = 0;
        long receivedBytes = 0;
        long sentBytes = 0;
        long receivedMessageCount = 0;
        long sentMessageCount = 0;
        long snapshotsSent = 0;
        long snapshotBytes = 0;
        long snapshotCulled = 0;
        long snapshotVisibleEntities = 0;
        long snapshotMaxVisibleEntities = 0;
        long snapshotClientByteSamples = 0;
        long snapshotClientBytesMax = 0;
        long loginAccepted = 0;
        long loginRejected = 0;
        double loginTotalMs = 0;
        double loginMaxMs = 0;

        foreach (var bucket in _buckets)
        {
            if (bucket.Second < oldestSecond || bucket.Second > nowSecond)
            {
                continue;
            }

            tickCount += bucket.TickCount;
            tickTotalMs += bucket.TickTotalMs;
            tickMaxMs = Math.Max(tickMaxMs, bucket.TickMaxMs);
            tickScheduleDriftTotalMs += bucket.TickScheduleDriftTotalMs;
            tickScheduleDriftMaxMs = Math.Max(tickScheduleDriftMaxMs, bucket.TickScheduleDriftMaxMs);
            AddTickBudget(tickBudgetTotalMs, tickBudgetMaxMs, bucket.TickBudgetTotalMs, bucket.TickBudgetMaxMs);
            networkErrors += bucket.NetworkErrors;
            badPackets += bucket.BadPackets;
            sendFailures += bucket.SendFailures;
            runtimeFaults += bucket.RuntimeFaults;
            receivedBytes += bucket.ReceivedBytes;
            sentBytes += bucket.SentBytes;
            receivedMessageCount += bucket.ReceivedMessageCount;
            sentMessageCount += bucket.SentMessageCount;
            snapshotsSent += bucket.SnapshotsSent;
            snapshotBytes += bucket.SnapshotBytes;
            snapshotCulled += bucket.SnapshotCulled;
            snapshotVisibleEntities += bucket.SnapshotVisibleEntities;
            snapshotMaxVisibleEntities = Math.Max(snapshotMaxVisibleEntities, bucket.SnapshotMaxVisibleEntities);
            snapshotClientByteSamples += bucket.SnapshotClientByteSamples;
            snapshotClientBytesMax = Math.Max(snapshotClientBytesMax, bucket.SnapshotClientBytesMax);
            loginAccepted += bucket.LoginAccepted;
            loginRejected += bucket.LoginRejected;
            loginTotalMs += bucket.LoginTotalMs;
            loginMaxMs = Math.Max(loginMaxMs, bucket.LoginMaxMs);

            for (var i = 0; i < MessageTypeCount; i++)
            {
                receivedMessages[i] += bucket.ReceivedMessages[i];
                sentMessages[i] += bucket.SentMessages[i];
            }
        }

        return new MetricsWindowSnapshot(
            window,
            effectiveSeconds,
            tickCount,
            tickCount == 0 ? 0 : tickTotalMs / tickCount,
            tickMaxMs,
            tickCount == 0 ? 0 : tickScheduleDriftTotalMs / tickCount,
            tickScheduleDriftMaxMs,
            AverageBudget(tickBudgetTotalMs, tickCount),
            MaxBudget(tickBudgetMaxMs),
            networkErrors,
            badPackets,
            sendFailures,
            runtimeFaults,
            receivedBytes,
            sentBytes,
            receivedMessageCount,
            sentMessageCount,
            snapshotsSent,
            snapshotBytes,
            snapshotCulled,
            snapshotsSent == 0 ? 0 : (double)snapshotVisibleEntities / snapshotsSent,
            snapshotMaxVisibleEntities,
            snapshotClientByteSamples == 0 ? 0 : (double)snapshotBytes / snapshotClientByteSamples,
            snapshotClientBytesMax,
            loginAccepted,
            loginRejected,
            loginAccepted + loginRejected == 0 ? 0 : loginTotalMs / (loginAccepted + loginRejected),
            loginMaxMs,
            receivedMessages,
            sentMessages);
    }

    public string FormatStateSummary(int peers, int players, uint serverTick, string syntheticLoadStatus)
    {
        var snapshot = Capture();
        return $"metrics state: uptime={FormatDuration(snapshot.Uptime)}, tick={serverTick}, peers={peers}, players={players}, {syntheticLoadStatus}";
    }

    public string FormatWindowSummary(TimeSpan window)
    {
        var snapshot = CaptureWindow(window);
        var seconds = snapshot.Seconds;
        return $"metrics {FormatWindowLabel(window)}: " +
            $"tick/s={Rate(snapshot.TickCount, seconds):0.0}, " +
            $"tickMs avg/max={snapshot.TickAverageMs:0.00}/{snapshot.TickMaxMs:0.00}, " +
            $"driftMs avg/max={snapshot.TickScheduleDriftAverageMs:0.00}/{snapshot.TickScheduleDriftMaxMs:0.00}, " +
            $"budgetMs move/aoi/ser/net/persist/other={FormatBudget(snapshot.TickBudgetAverageMs)}, " +
            $"snap/s={Rate(snapshot.SnapshotsSent, seconds):0.0}, " +
            $"visible avg/max={snapshot.SnapshotAverageVisibleEntities:0.0}/{snapshot.SnapshotMaxVisibleEntities}, " +
            $"clientBytes avg/max={snapshot.SnapshotClientBytesAverage:0.0}/{snapshot.SnapshotClientBytesMax}, " +
            $"culled/s={Rate(snapshot.SnapshotCulled, seconds):0.0}, " +
            $"out={ToKbps(snapshot.SentBytes, seconds):0.0}kbps, in={ToKbps(snapshot.ReceivedBytes, seconds):0.0}kbps, " +
            $"recv/s={Rate(snapshot.ReceivedMessageCount, seconds):0.0}, sent/s={Rate(snapshot.SentMessageCount, seconds):0.0}, " +
            $"move/s={Rate(Count(snapshot.ReceivedMessages, MessageType.MoveStep), seconds):0.0}, " +
            $"chat/s={Rate(Count(snapshot.ReceivedMessages, MessageType.ChatSend), seconds):0.0}, " +
            $"sendFail/s={Rate(snapshot.SendFailures, seconds):0.0}, bad/s={Rate(snapshot.BadPackets, seconds):0.0}, netErr/s={Rate(snapshot.NetworkErrors, seconds):0.0}, runtimeFault/s={Rate(snapshot.RuntimeFaults, seconds):0.0}, " +
            $"login/s={Rate(snapshot.LoginAccepted + snapshot.LoginRejected, seconds):0.0}, loginMs avg/max={snapshot.LoginAverageMs:0.0}/{snapshot.LoginMaxMs:0.0}ms";
    }

    public string FormatTotalSummary()
    {
        var snapshot = Capture();
        var seconds = Math.Max(0.001, snapshot.Uptime.TotalSeconds);
        return "metrics total: " +
            $"tickMs last/avg/max={snapshot.TickLastMs:0.00}/{snapshot.TickAverageMs:0.00}/{snapshot.TickMaxMs:0.00}, " +
            $"driftMs avg/max={snapshot.TickScheduleDriftAverageMs:0.00}/{snapshot.TickScheduleDriftMaxMs:0.00}, " +
            $"budgetMs avg={FormatBudget(snapshot.TickBudgetAverageMs)}, budgetMs max={FormatBudget(snapshot.TickBudgetMaxMs)}, " +
            $"snap/s(avg)={Rate(snapshot.SnapshotsSent, seconds):0.0}, snapshots={snapshot.SnapshotsSent}, " +
            $"visible avg/max={snapshot.SnapshotAverageVisibleEntities:0.0}/{snapshot.SnapshotMaxVisibleEntities}, " +
            $"clientBytes avg/max={snapshot.SnapshotClientBytesAverage:0.0}/{snapshot.SnapshotClientBytesMax}, " +
            $"outAvg={ToKbps(snapshot.SentBytes, seconds):0.0}kbps, inAvg={ToKbps(snapshot.ReceivedBytes, seconds):0.0}kbps, " +
            $"sendFail={snapshot.SendFailures}, badPackets={snapshot.BadPackets}, netErr={snapshot.NetworkErrors}, runtimeFaults={snapshot.RuntimeFaults}, " +
            $"login={snapshot.LoginAccepted}/{snapshot.LoginRejected}, loginMs avg/max={snapshot.LoginAverageMs:0.0}/{snapshot.LoginMaxMs:0.0}ms";
    }

    public string FormatMessageSummary()
    {
        var snapshot = Capture();
        var received = FormatMessageCounts(snapshot.ReceivedMessages);
        var sent = FormatMessageCounts(snapshot.SentMessages);
        return $"message metrics: received[{received}], sent[{sent}]";
    }

    private MetricBucket CurrentBucket()
    {
        var second = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var index = (int)(second % WindowBucketCount);
        var bucket = _buckets[index];
        if (bucket.Second != second)
        {
            bucket.Reset(second);
        }

        return bucket;
    }

    private static long Count(IReadOnlyList<long> counts, MessageType type)
    {
        var index = (int)type;
        return (uint)index < counts.Count ? counts[index] : 0;
    }

    private static void AddTickBudget(double[] totals, double[] maxes, TickBudgetSample budget)
    {
        for (var i = 0; i < TickBudgetRecorder.TickBudgetCategoryCount; i++)
        {
            var value = budget.Get((TickBudgetCategory)i);
            totals[i] += value;
            maxes[i] = Math.Max(maxes[i], value);
        }
    }

    private static void AddTickBudget(double[] totals, double[] maxes, IReadOnlyList<double> sourceTotals, IReadOnlyList<double> sourceMaxes)
    {
        for (var i = 0; i < TickBudgetRecorder.TickBudgetCategoryCount; i++)
        {
            totals[i] += sourceTotals[i];
            maxes[i] = Math.Max(maxes[i], sourceMaxes[i]);
        }
    }

    private static TickBudgetSample AverageBudget(IReadOnlyList<double> totals, long tickCount)
    {
        if (tickCount <= 0)
        {
            return TickBudgetSample.Zero;
        }

        return new TickBudgetSample(
            totals[(int)TickBudgetCategory.Movement] / tickCount,
            totals[(int)TickBudgetCategory.Aoi] / tickCount,
            totals[(int)TickBudgetCategory.Serialize] / tickCount,
            totals[(int)TickBudgetCategory.Network] / tickCount,
            totals[(int)TickBudgetCategory.Persistence] / tickCount,
            totals[(int)TickBudgetCategory.Other] / tickCount);
    }

    private static TickBudgetSample MaxBudget(IReadOnlyList<double> maxes)
    {
        return new TickBudgetSample(
            maxes[(int)TickBudgetCategory.Movement],
            maxes[(int)TickBudgetCategory.Aoi],
            maxes[(int)TickBudgetCategory.Serialize],
            maxes[(int)TickBudgetCategory.Network],
            maxes[(int)TickBudgetCategory.Persistence],
            maxes[(int)TickBudgetCategory.Other]);
    }

    private static string FormatBudget(TickBudgetSample budget)
    {
        return $"{budget.MovementMs:0.00}/{budget.AoiMs:0.00}/{budget.SerializeMs:0.00}/{budget.NetworkMs:0.00}/{budget.PersistenceMs:0.00}/{budget.OtherMs:0.00}";
    }

    private static string FormatMessageCounts(IReadOnlyList<long> counts)
    {
        var parts = new List<string>();
        foreach (MessageType type in Enum.GetValues<MessageType>())
        {
            var index = (int)type;
            if ((uint)index < counts.Count && counts[index] > 0)
            {
                parts.Add($"{type}={counts[index]}");
            }
        }

        return parts.Count == 0 ? "none" : string.Join(", ", parts);
    }

    private static double Rate(long count, double seconds)
    {
        return count / Math.Max(0.001, seconds);
    }

    private static double ToKbps(long bytes, double seconds)
    {
        return (bytes * 8d / 1000d) / Math.Max(0.001, seconds);
    }

    private static string FormatWindowLabel(TimeSpan window)
    {
        return $"{window.TotalSeconds:0}s";
    }

    private static string FormatDuration(TimeSpan duration)
    {
        return duration.TotalSeconds < 60
            ? $"{duration.TotalSeconds:0.#}s"
            : $"{duration.TotalMinutes:0.#}m";
    }

    private sealed class MetricBucket
    {
        public readonly long[] ReceivedMessages = new long[MessageTypeCount];
        public readonly long[] SentMessages = new long[MessageTypeCount];

        public long Second { get; private set; } = long.MinValue;
        public long TickCount { get; set; }
        public double TickTotalMs { get; set; }
        public double TickMaxMs { get; set; }
        public double TickScheduleDriftTotalMs { get; set; }
        public double TickScheduleDriftMaxMs { get; set; }
        public readonly double[] TickBudgetTotalMs = new double[TickBudgetRecorder.TickBudgetCategoryCount];
        public readonly double[] TickBudgetMaxMs = new double[TickBudgetRecorder.TickBudgetCategoryCount];
        public long NetworkErrors { get; set; }
        public long BadPackets { get; set; }
        public long SendFailures { get; set; }
        public long RuntimeFaults { get; set; }
        public long ReceivedBytes { get; set; }
        public long SentBytes { get; set; }
        public long ReceivedMessageCount { get; set; }
        public long SentMessageCount { get; set; }
        public long SnapshotsSent { get; set; }
        public long SnapshotBytes { get; set; }
        public long SnapshotCulled { get; set; }
        public long SnapshotVisibleEntities { get; set; }
        public long SnapshotMaxVisibleEntities { get; set; }
        public long SnapshotClientByteSamples { get; set; }
        public long SnapshotClientBytesMax { get; set; }
        public long LoginAccepted { get; set; }
        public long LoginRejected { get; set; }
        public double LoginTotalMs { get; set; }
        public double LoginMaxMs { get; set; }

        public void Reset(long second)
        {
            Second = second;
            TickCount = 0;
            TickTotalMs = 0;
            TickMaxMs = 0;
            TickScheduleDriftTotalMs = 0;
            TickScheduleDriftMaxMs = 0;
            NetworkErrors = 0;
            BadPackets = 0;
            SendFailures = 0;
            RuntimeFaults = 0;
            ReceivedBytes = 0;
            SentBytes = 0;
            ReceivedMessageCount = 0;
            SentMessageCount = 0;
            SnapshotsSent = 0;
            SnapshotBytes = 0;
            SnapshotCulled = 0;
            SnapshotVisibleEntities = 0;
            SnapshotMaxVisibleEntities = 0;
            SnapshotClientByteSamples = 0;
            SnapshotClientBytesMax = 0;
            LoginAccepted = 0;
            LoginRejected = 0;
            LoginTotalMs = 0;
            LoginMaxMs = 0;
            Array.Clear(TickBudgetTotalMs);
            Array.Clear(TickBudgetMaxMs);
            Array.Clear(ReceivedMessages);
            Array.Clear(SentMessages);
        }
    }
}

public sealed record MetricsSnapshot(
    TimeSpan Uptime,
    long TickCount,
    double TickLastMs,
    double TickAverageMs,
    double TickMaxMs,
    double TickScheduleDriftAverageMs,
    double TickScheduleDriftMaxMs,
    TickBudgetSample TickBudgetAverageMs,
    TickBudgetSample TickBudgetMaxMs,
    long PeersConnected,
    long PeersDisconnected,
    long NetworkErrors,
    long BadPackets,
    long SendFailures,
    long RuntimeFaults,
    long ReceivedBytes,
    long SentBytes,
    long SnapshotsSent,
    long SnapshotBytes,
    long SnapshotCulled,
    double SnapshotAverageVisibleEntities,
    long SnapshotMaxVisibleEntities,
    double SnapshotClientBytesAverage,
    long SnapshotClientBytesMax,
    long LoginAccepted,
    long LoginRejected,
    double LoginAverageMs,
    double LoginMaxMs,
    IReadOnlyList<long> ReceivedMessages,
    IReadOnlyList<long> SentMessages,
    IReadOnlyList<long> ReceivedBytesByType,
    IReadOnlyList<long> SentBytesByType);

public sealed record MetricsWindowSnapshot(
    TimeSpan Window,
    double Seconds,
    long TickCount,
    double TickAverageMs,
    double TickMaxMs,
    double TickScheduleDriftAverageMs,
    double TickScheduleDriftMaxMs,
    TickBudgetSample TickBudgetAverageMs,
    TickBudgetSample TickBudgetMaxMs,
    long NetworkErrors,
    long BadPackets,
    long SendFailures,
    long RuntimeFaults,
    long ReceivedBytes,
    long SentBytes,
    long ReceivedMessageCount,
    long SentMessageCount,
    long SnapshotsSent,
    long SnapshotBytes,
    long SnapshotCulled,
    double SnapshotAverageVisibleEntities,
    long SnapshotMaxVisibleEntities,
    double SnapshotClientBytesAverage,
    long SnapshotClientBytesMax,
    long LoginAccepted,
    long LoginRejected,
    double LoginAverageMs,
    double LoginMaxMs,
    IReadOnlyList<long> ReceivedMessages,
    IReadOnlyList<long> SentMessages);
