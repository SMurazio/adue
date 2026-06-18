using System.Diagnostics;
using System.Globalization;
using System.Text;
using Mmo.Shared.Domain;

namespace Mmo.Server.Runtime;

/// <summary>
/// Flag-gated, observe-only server cadence trace (S33). When <c>MMO_DEBUG_CADENCE_LOG</c> is set it
/// writes two gitignored CSVs under the repo-root <c>.run/</c> directory:
/// <list type="bullet">
/// <item><c>server-cadence.csv</c> — one row per server tick (broadcast cadence + moving count).</item>
/// <item><c>server-steps.csv</c> — one row per entity tile change (per-entity step interval).</item>
/// </list>
/// Unset (the default, and the case in normal runs) means no files are opened and zero behaviour
/// change. The trace never inspects or mutates tick/movement state beyond reading tile positions.
/// </summary>
internal sealed class ServerCadenceTrace : IDisposable
{
    private const string EnableEnvironmentKey = "MMO_DEBUG_CADENCE_LOG";

    private readonly StreamWriter? _cadenceWriter;
    private readonly StreamWriter? _stepsWriter;

    // Per-entity last-seen tile + timestamp of the previous tile change, keyed by stable entity id.
    private readonly Dictionary<ulong, EntityStepState> _entityStates = [];
    private readonly Action<string> _warn;

    private long _lastBroadcastTimestamp;

    private ServerCadenceTrace(StreamWriter? cadenceWriter, StreamWriter? stepsWriter, Action<string> warn)
    {
        _cadenceWriter = cadenceWriter;
        _stepsWriter = stepsWriter;
        _warn = warn;
    }

    public bool Enabled => _cadenceWriter is not null;

    public static ServerCadenceTrace FromEnvironment(Action<string>? warn = null)
    {
        warn ??= Log.Warn;
        if (!ReadBool(EnableEnvironmentKey))
        {
            return new ServerCadenceTrace(null, null, warn);
        }

        try
        {
            var runDir = ResolveRunDirectory();
            Directory.CreateDirectory(runDir);

            var cadence = new StreamWriter(Path.Combine(runDir, "server-cadence.csv"), append: false)
            {
                AutoFlush = false
            };
            cadence.WriteLine("tick,wallClockMs,sinceLastBroadcastMs,snapshotsSent,movingEntities");

            var steps = new StreamWriter(Path.Combine(runDir, "server-steps.csv"), append: false)
            {
                AutoFlush = false
            };
            steps.WriteLine("tick,wallClockMs,entityId,networkId,stepIntervalMs,tileX,tileY");

            return new ServerCadenceTrace(cadence, steps, warn);
        }
        catch (Exception exception)
        {
            warn($"Could not open .run/server-cadence.csv: {exception.Message}");
            return new ServerCadenceTrace(null, null, warn);
        }
    }

    /// <summary>
    /// Records one tick. <paramref name="elapsed"/> is Stopwatch elapsed since server start;
    /// <paramref name="entities"/> is the live entity snapshot used for the broadcast this tick;
    /// <paramref name="snapshotsSent"/> is the number of sessions that received a snapshot packet.
    /// </summary>
    public void RecordTick(
        uint tick,
        TimeSpan elapsed,
        IReadOnlyList<WorldEntity> entities,
        int snapshotsSent)
    {
        if (_cadenceWriter is null)
        {
            return;
        }

        var now = Stopwatch.GetTimestamp();
        var sinceLastBroadcastMs = _lastBroadcastTimestamp == 0
            ? 0d
            : Stopwatch.GetElapsedTime(_lastBroadcastTimestamp, now).TotalMilliseconds;
        _lastBroadcastTimestamp = now;

        var wallClockMs = elapsed.TotalMilliseconds;
        var movingEntities = 0;

        for (var i = 0; i < entities.Count; i++)
        {
            var entity = entities[i];
            var tile = entity.Tile;
            if (_entityStates.TryGetValue(entity.Id, out var state))
            {
                if (state.Tile.X != tile.X || state.Tile.Y != tile.Y)
                {
                    movingEntities++;
                    var stepIntervalMs = state.LastStepTimestamp == 0
                        ? 0d
                        : Stopwatch.GetElapsedTime(state.LastStepTimestamp, now).TotalMilliseconds;
                    WriteStepRow(tick, wallClockMs, entity, stepIntervalMs, tile);
                    _entityStates[entity.Id] = new EntityStepState(tile, now);
                }
            }
            else
            {
                // First sighting: record baseline without emitting a (spurious) step event.
                _entityStates[entity.Id] = new EntityStepState(tile, now);
            }
        }

        WriteCadenceRow(tick, wallClockMs, sinceLastBroadcastMs, snapshotsSent, movingEntities);
    }

    public void Flush()
    {
        TryFlush(_cadenceWriter);
        TryFlush(_stepsWriter);
    }

    public void Dispose()
    {
        TryDispose(_cadenceWriter);
        TryDispose(_stepsWriter);
    }

    private void WriteCadenceRow(uint tick, double wallClockMs, double sinceLastBroadcastMs, int snapshotsSent, int movingEntities)
    {
        var row = string.Create(CultureInfo.InvariantCulture,
            $"{tick},{wallClockMs:0.###},{sinceLastBroadcastMs:0.###},{snapshotsSent},{movingEntities}");
        try
        {
            _cadenceWriter!.WriteLine(row);
        }
        catch (IOException exception)
        {
            _warn($"server-cadence.csv write failed: {exception.Message}");
        }
    }

    private void WriteStepRow(uint tick, double wallClockMs, WorldEntity entity, double stepIntervalMs, TileCoord tile)
    {
        if (_stepsWriter is null)
        {
            return;
        }

        var row = string.Create(CultureInfo.InvariantCulture,
            $"{tick},{wallClockMs:0.###},{entity.Id},{entity.NetworkId},{stepIntervalMs:0.###},{tile.X},{tile.Y}");
        try
        {
            _stepsWriter.WriteLine(row);
        }
        catch (IOException exception)
        {
            _warn($"server-steps.csv write failed: {exception.Message}");
        }
    }

    private static void TryFlush(StreamWriter? writer)
    {
        if (writer is null)
        {
            return;
        }

        try
        {
            writer.Flush();
        }
        catch (IOException)
        {
            // Best effort; tracing must never crash the server.
        }
    }

    private static void TryDispose(StreamWriter? writer)
    {
        if (writer is null)
        {
            return;
        }

        try
        {
            writer.Flush();
            writer.Dispose();
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    private static string ResolveRunDirectory()
    {
        // Walk up from the working directory to the repo root (the folder containing Mmo.sln) and
        // place the trace under the gitignored repo-root .run/, matching the client frame CSV.
        var current = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Mmo.sln")))
            {
                return Path.Combine(current.FullName, ".run");
            }

            current = current.Parent;
        }

        return Path.GetFullPath(".run");
    }

    private static bool ReadBool(string key)
    {
        var value = Environment.GetEnvironmentVariable(key);
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "on" => true,
            _ => false
        };
    }

    private readonly record struct EntityStepState(TileCoord Tile, long LastStepTimestamp);
}
