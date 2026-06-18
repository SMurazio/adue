using System.Diagnostics;
using Mmo.Tools.Stress;

StressOptions options;
try
{
    options = StressOptions.FromArgs(args);
}
catch (ArgumentException exception)
{
    Console.WriteLine(exception.Message);
    Console.WriteLine();
    PrintUsage();
    return 2;
}

if (options.ShowHelp)
{
    PrintUsage();
    return 0;
}

using var shutdown = new CancellationTokenSource();
using var duration = new CancellationTokenSource(options.Duration);
using var linked = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token, duration.Token);

Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    shutdown.Cancel();
};

var stats = new RunStats();
var clients = new List<LoadClient>(options.Clients);
var startedAt = Stopwatch.StartNew();
var nextSpawnAt = TimeSpan.Zero;
var spawnInterval = TimeSpan.FromSeconds(1d / options.SpawnRatePerSecond);
var nextReportAt = options.ReportInterval;
var lastReportAt = TimeSpan.Zero;
var lastReport = stats.Capture();
var spawned = 0;

Console.WriteLine($"Stress target: {options.Host}:{options.Port}");
Console.WriteLine($"Clients={options.Clients} duration={FormatDuration(options.Duration)} spawnRate={options.SpawnRatePerSecond:0.##}/s seed={options.Seed}");
Console.WriteLine($"Movement every {FormatDuration(options.MoveInterval)}, direction every {FormatDuration(options.DirectionInterval)}, chat={(options.ChatInterval == TimeSpan.Zero ? "off" : FormatDuration(options.ChatInterval))}");
Console.WriteLine($"Pass criteria: authRate>={options.MinAuthRate:P0}, errors<={options.MaxErrors}");
Console.WriteLine();

try
{
    while (!linked.IsCancellationRequested)
    {
        var elapsed = startedAt.Elapsed;

        while (spawned < options.Clients && elapsed >= nextSpawnAt)
        {
            var client = new LoadClient(spawned, options, stats, new Random(options.Seed + spawned));
            client.Start();
            clients.Add(client);
            spawned++;
            nextSpawnAt += spawnInterval;
        }

        foreach (var client in clients)
        {
            client.Poll(elapsed);
        }

        if (elapsed >= nextReportAt)
        {
            var current = stats.Capture();
            PrintReport(elapsed, spawned, clients, current, lastReport, elapsed - lastReportAt);
            lastReport = current;
            lastReportAt = elapsed;
            nextReportAt += options.ReportInterval;
        }

        await Task.Delay(5, linked.Token);
    }
}
catch (OperationCanceledException) when (linked.IsCancellationRequested)
{
}
finally
{
    foreach (var client in clients)
    {
        client.Stop();
    }
}

var final = stats.Capture();
Console.WriteLine();
PrintSummary(startedAt.Elapsed, spawned, final);

var authRate = spawned == 0 ? 0 : (double)final.LoginAccepted / spawned;
var errors = final.NetworkErrors + final.ServerErrors;
return authRate < options.MinAuthRate || errors > options.MaxErrors ? 1 : 0;

static void PrintReport(
    TimeSpan elapsed,
    int spawned,
    IReadOnlyCollection<LoadClient> clients,
    StatsSnapshot current,
    StatsSnapshot previous,
    TimeSpan interval)
{
    var intervalSeconds = Math.Max(0.001, interval.TotalSeconds);
    var snapshotsPerSecond = (current.Snapshots - previous.Snapshots) / intervalSeconds;
    var sentKbps = ((current.SentBytes - previous.SentBytes) * 8d / 1000d) / intervalSeconds;
    var receivedKbps = ((current.ReceivedBytes - previous.ReceivedBytes) * 8d / 1000d) / intervalSeconds;
    var authenticated = clients.Count(client => client.IsAuthenticated);
    var activePeers = current.PeersConnected - current.PeersDisconnected;

    Console.WriteLine(
        $"t={elapsed.TotalSeconds,6:0.0}s spawned={spawned,4} peers={activePeers,4} authed={authenticated,4} " +
        $"snap/s={snapshotsPerSecond,7:0.0} in={receivedKbps,8:0.0}kbps out={sentKbps,7:0.0}kbps " +
        $"avgPing={current.AverageLatencyMs,5:0.0}ms maxPing={current.LatencyMaxMs,4}ms errors={current.NetworkErrors + current.ServerErrors}");
}

static void PrintSummary(TimeSpan elapsed, int spawned, StatsSnapshot final)
{
    Console.WriteLine();
    Console.WriteLine("Summary");
    Console.WriteLine($"  elapsed: {FormatDuration(elapsed)}");
    Console.WriteLine($"  spawned: {spawned}");
    Console.WriteLine($"  connected: {final.PeersConnected}");
    Console.WriteLine($"  disconnected: {final.PeersDisconnected}");
    Console.WriteLine($"  logins accepted/rejected: {final.LoginAccepted}/{final.LoginRejected}");
    Console.WriteLine($"  snapshots: {final.Snapshots} total, max entities in one snapshot: {final.MaxSnapshotEntities}");
    Console.WriteLine($"  protocol messages sent/received: {final.SentMessages}/{final.ReceivedMessages}");
    Console.WriteLine($"  protocol bytes sent/received: {final.SentBytes}/{final.ReceivedBytes}");
    Console.WriteLine($"  avg/max latency: {final.AverageLatencyMs:0.0}ms/{final.LatencyMaxMs}ms");
    Console.WriteLine($"  server/network errors: {final.ServerErrors}/{final.NetworkErrors}");
}

static void PrintUsage()
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  dotnet run --project .\\src\\Mmo.Tools.Stress\\Mmo.Tools.Stress.csproj -- [options]");
    Console.WriteLine();
    Console.WriteLine("Options:");
    Console.WriteLine("  --host=127.0.0.1              Game server host");
    Console.WriteLine("  --port=7777                   Game server UDP port");
    Console.WriteLine("  --key=local-dev               LiteNetLib connection key");
    Console.WriteLine("  --clients=50                  Synthetic client count");
    Console.WriteLine("  --duration=60s                Run duration, supports ms/s/m or TimeSpan");
    Console.WriteLine("  --spawn-rate=25               New clients per second");
    Console.WriteLine("  --move-interval=250ms         How often each client sends movement input");
    Console.WriteLine("  --direction-interval=1s       How often each client picks a new direction");
    Console.WriteLine("  --report-interval=5s          Console report cadence");
    Console.WriteLine("  --chat-interval=0             Optional chat send interval per client; 0 disables chat");
    Console.WriteLine("  --min-auth-rate=1             Required accepted login ratio, 0 to 1");
    Console.WriteLine("  --max-errors=0                Allowed server/network errors before failing");
    Console.WriteLine("  --name-prefix=LoadHHMMSS      Character/account name prefix");
    Console.WriteLine("  --seed=123                    Deterministic movement seed");
}

static string FormatDuration(TimeSpan value)
{
    return value.TotalMilliseconds < 1000
        ? $"{value.TotalMilliseconds:0}ms"
        : $"{value.TotalSeconds:0.###}s";
}
