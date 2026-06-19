using System.Globalization;
using Mmo.Shared.Domain;

namespace Mmo.Client.Core;

internal sealed class ClientMovementTrace
{
    private readonly Action<string> _write;

    public ClientMovementTrace(bool enabled, Action<string>? write = null)
    {
        Enabled = enabled;
        _write = write ?? Console.WriteLine;
    }

    public bool Enabled { get; }

    public MovementDebugSnapshot Snapshot { get; private set; } = MovementDebugSnapshot.Empty;

    public static ClientMovementTrace FromEnvironment()
    {
        return new ClientMovementTrace(ReadBool("MMO_DEBUG_MOVEMENT", false));
    }

    public void UpdateLatency(int latency)
    {
        // Snapshot state is tracked unconditionally so live debug HUDs (e.g. the Godot F3 panel) can
        // read interpolation/movement state without enabling the console trace. Only the console
        // output below is gated by Enabled.
        Snapshot = Snapshot with { LastLatencyMs = latency };
    }

    public void MoveSent(uint sequence, bool moving, Direction8 direction)
    {
        var sentAt = DateTimeOffset.UtcNow;
        Snapshot = Snapshot with
        {
            LastSentSequence = sequence,
            LastSentDirection = direction,
            LastSentAtUtc = sentAt
        };

        if (!Enabled)
        {
            return;
        }

        _write(
            "mmo_trace side=client event=move_intent" +
            $" ts={Timestamp(sentAt)} seq={sequence.ToString(CultureInfo.InvariantCulture)} moving={(moving ? "true" : "false")} dir={direction}");
    }

    public void TileConfirmed(
        uint networkId,
        TileCoord tile,
        uint snapshotSequence,
        DateTimeOffset arrivedAt,
        int queueDepth,
        double effectiveCadenceMs,
        RenderPosition renderPosition)
    {
        Snapshot = Snapshot with
        {
            LastConfirmedNetworkId = networkId,
            LastConfirmedTile = tile,
            LastConfirmedSnapshotSequence = snapshotSequence,
            LastConfirmedAtUtc = arrivedAt,
            QueueDepth = queueDepth,
            EffectiveCadenceMs = effectiveCadenceMs,
            RenderPosition = renderPosition
        };

        if (!Enabled)
        {
            return;
        }

        _write(
            "mmo_trace side=client event=tile_confirmed" +
            $" ts={Timestamp(arrivedAt)} networkId={networkId.ToString(CultureInfo.InvariantCulture)}" +
            $" snapshot={snapshotSequence.ToString(CultureInfo.InvariantCulture)} tile={FormatTile(tile)}" +
            $" queueDepth={queueDepth.ToString(CultureInfo.InvariantCulture)} cadenceMs={effectiveCadenceMs.ToString("0.###", CultureInfo.InvariantCulture)}" +
            $" render={renderPosition.X.ToString("0.###", CultureInfo.InvariantCulture)},{renderPosition.Y.ToString("0.###", CultureInfo.InvariantCulture)}");
    }

    public void FrameHitch(
        double durationMs,
        int gc0,
        int gc1,
        int gc2,
        int visibleEntities,
        ClientConnectionState state)
    {
        if (!Enabled)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        _write(
            "mmo_trace side=client event=frame_hitch" +
            $" ts={Timestamp(now)} durationMs={durationMs.ToString("0.###", CultureInfo.InvariantCulture)}" +
            $" gc0={gc0.ToString(CultureInfo.InvariantCulture)} gc1={gc1.ToString(CultureInfo.InvariantCulture)} gc2={gc2.ToString(CultureInfo.InvariantCulture)}" +
            $" queueDepth={Snapshot.QueueDepth.ToString(CultureInfo.InvariantCulture)} cadenceMs={Snapshot.EffectiveCadenceMs.ToString("0.###", CultureInfo.InvariantCulture)}" +
            $" latencyMs={Snapshot.LastLatencyMs.ToString(CultureInfo.InvariantCulture)} visible={visibleEntities.ToString(CultureInfo.InvariantCulture)} state={state}" +
            $" render={Snapshot.RenderPosition.X.ToString("0.###", CultureInfo.InvariantCulture)},{Snapshot.RenderPosition.Y.ToString("0.###", CultureInfo.InvariantCulture)}");
    }

    private static bool ReadBool(string key, bool fallback)
    {
        var value = Environment.GetEnvironmentVariable(key);
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "on" => true,
            "0" or "false" or "no" or "off" => false,
            _ => fallback
        };
    }

    private static string Timestamp(DateTimeOffset timestamp)
    {
        return timestamp.ToString("O", CultureInfo.InvariantCulture);
    }

    private static string FormatTile(TileCoord tile)
    {
        return $"{tile.X.ToString(CultureInfo.InvariantCulture)},{tile.Y.ToString(CultureInfo.InvariantCulture)}";
    }
}
