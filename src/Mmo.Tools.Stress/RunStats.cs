namespace Mmo.Tools.Stress;

public sealed class RunStats
{
    public long PeersConnected { get; private set; }
    public long PeersDisconnected { get; private set; }
    public long LoginAccepted { get; private set; }
    public long LoginRejected { get; private set; }
    public long ServerHellos { get; private set; }
    public long Snapshots { get; private set; }
    public long SnapshotEntities { get; private set; }
    public long MaxSnapshotEntities { get; private set; }
    public long ChatBroadcasts { get; private set; }
    public long ServerErrors { get; private set; }
    public long NetworkErrors { get; private set; }
    public long SentMessages { get; private set; }
    public long ReceivedMessages { get; private set; }
    public long SentBytes { get; private set; }
    public long ReceivedBytes { get; private set; }
    public long LatencySamples { get; private set; }
    public long LatencyTotalMs { get; private set; }
    public long LatencyMaxMs { get; private set; }

    public void RecordPeerConnected()
    {
        PeersConnected++;
    }

    public void RecordPeerDisconnected()
    {
        PeersDisconnected++;
    }

    public void RecordServerHello()
    {
        ServerHellos++;
    }

    public void RecordLoginAccepted()
    {
        LoginAccepted++;
    }

    public void RecordLoginRejected()
    {
        LoginRejected++;
    }

    public void RecordSnapshot(int entityCount)
    {
        Snapshots++;
        SnapshotEntities += entityCount;
        MaxSnapshotEntities = Math.Max(MaxSnapshotEntities, entityCount);
    }

    public void RecordChatBroadcast()
    {
        ChatBroadcasts++;
    }

    public void RecordServerError()
    {
        ServerErrors++;
    }

    public void RecordNetworkError()
    {
        NetworkErrors++;
    }

    public void RecordSent(int byteCount)
    {
        SentMessages++;
        SentBytes += byteCount;
    }

    public void RecordReceived(int byteCount)
    {
        ReceivedMessages++;
        ReceivedBytes += byteCount;
    }

    public void RecordLatency(int latencyMs)
    {
        LatencySamples++;
        LatencyTotalMs += latencyMs;
        LatencyMaxMs = Math.Max(LatencyMaxMs, latencyMs);
    }

    public StatsSnapshot Capture()
    {
        return new StatsSnapshot(
            PeersConnected,
            PeersDisconnected,
            LoginAccepted,
            LoginRejected,
            ServerHellos,
            Snapshots,
            SnapshotEntities,
            MaxSnapshotEntities,
            ChatBroadcasts,
            ServerErrors,
            NetworkErrors,
            SentMessages,
            ReceivedMessages,
            SentBytes,
            ReceivedBytes,
            LatencySamples,
            LatencyTotalMs,
            LatencyMaxMs);
    }
}

public readonly record struct StatsSnapshot(
    long PeersConnected,
    long PeersDisconnected,
    long LoginAccepted,
    long LoginRejected,
    long ServerHellos,
    long Snapshots,
    long SnapshotEntities,
    long MaxSnapshotEntities,
    long ChatBroadcasts,
    long ServerErrors,
    long NetworkErrors,
    long SentMessages,
    long ReceivedMessages,
    long SentBytes,
    long ReceivedBytes,
    long LatencySamples,
    long LatencyTotalMs,
    long LatencyMaxMs)
{
    public double AverageLatencyMs => LatencySamples == 0 ? 0 : (double)LatencyTotalMs / LatencySamples;
}
