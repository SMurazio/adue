$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..\..')).Path
$localDotnet = Join-Path $root '.tools\dotnet\dotnet.exe'
$dotnet = if (Test-Path $localDotnet) { $localDotnet } else { 'dotnet' }
$projectDir = Join-Path $root '.run\MovementDebugTrace'
$project = Join-Path $projectDir 'MovementDebugTrace.csproj'
$program = Join-Path $projectDir 'Program.cs'

New-Item -ItemType Directory -Force -Path $projectDir | Out-Null

Set-Content -LiteralPath $project -Encoding ASCII -Value @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <UseAppHost>false</UseAppHost>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\Mmo.Client.Core\Mmo.Client.Core.csproj" />
    <ProjectReference Include="..\..\src\Mmo.Server\Mmo.Server.csproj" />
    <ProjectReference Include="..\..\src\Mmo.Shared\Mmo.Shared.csproj" />
  </ItemGroup>
</Project>
'@

Set-Content -LiteralPath $program -Encoding ASCII -Value @'
using System.Net;
using System.Net.Sockets;
using Mmo.Client.Core;
using Mmo.Server.Configuration;
using Mmo.Server.Data;
using Mmo.Server.Runtime;
using Mmo.Shared.Domain;

var root = ReadString(args, "--root=", Directory.GetCurrentDirectory());
var dbPath = Path.Combine(root, ".run", "movement-debug-trace.db");
Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
if (File.Exists(dbPath))
{
    File.Delete(dbPath);
}

Environment.SetEnvironmentVariable("MMO_DEBUG_MOVEMENT", "1");
Environment.SetEnvironmentVariable("MMO_DEBUG_MOVEMENT_WATCH", "TraceMover");

var port = GetFreeUdpPort();
var connectionString = $"Data Source={dbPath}";
var migrationsPath = Path.Combine(root, "db", "sqlite");
var options = new ServerOptions(
    port,
    20,
    "movement-debug-trace",
    DatabaseProvider.Sqlite,
    connectionString,
    migrationsPath,
    64,
    64,
    50,
    15,
    30,
    150,
    SpawnDistribution.Clustered,
    new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "TraceMover" })
{
    DebugMovement = true,
    DebugMovementWatchNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "TraceMover" },
    DebugMovementHitchThresholdMultiplier = 1.1d
};

await new SqliteMigrationRunner(connectionString, migrationsPath).ApplyAsync(CancellationToken.None);
var server = new GameServer(options, new SqliteCharacterRepository(connectionString));
using var shutdown = new CancellationTokenSource();
var serverTask = server.RunAsync(shutdown.Token);

try
{
    await Task.Delay(150);
    using var mover = new MmoClient(new ClientConnectionOptions("127.0.0.1", port, options.ConnectionKey, "TraceMover", "TraceMover"));
    using var watcher = new MmoClient(new ClientConnectionOptions("127.0.0.1", port, options.ConnectionKey, "TraceWatcher", "TraceWatcher"));
    mover.Connect();
    watcher.Connect();

    await WaitUntilAsync(
        () => mover.IsLoggedIn
            && watcher.IsLoggedIn
            && mover.LocalNetworkId.HasValue
            && watcher.LocalNetworkId.HasValue,
        mover,
        watcher);

    await WaitUntilAsync(
        () => watcher.TryGetEntity(mover.LocalNetworkId!.Value, out _),
        mover,
        watcher);

    for (var i = 0; i < 4; i++)
    {
        mover.SendMoveIntent(true, Direction8.E);
        await WaitUntilWithTimeoutAsync(
            () => mover.MovementDebug.LastConfirmedSnapshotSequence > (uint)i,
            TimeSpan.FromSeconds(2),
            mover,
            watcher);
        await Task.Delay(75);
    }

    await PumpForAsync(TimeSpan.FromMilliseconds(400), mover, watcher);
    Console.WriteLine(
        "HARNESS result " +
        $"moverTile={mover.LocalTile} " +
        $"watcherSeesMover={watcher.TryGetEntity(mover.LocalNetworkId!.Value, out var seenMover)} " +
        $"seenTile={(seenMover.Tile.ToString() ?? "unknown")} " +
        $"lastSeq={mover.MovementDebug.LastSentSequence} " +
        $"confirmedSnapshot={mover.MovementDebug.LastConfirmedSnapshotSequence} " +
        $"queueDepth={mover.MovementDebug.QueueDepth} " +
        $"latencyMs={mover.MovementDebug.LastLatencyMs}");
}
finally
{
    shutdown.Cancel();
    await serverTask;
}

static int GetFreeUdpPort()
{
    using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
    return ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
}

static async Task WaitUntilAsync(Func<bool> condition, params MmoClient[] clients)
{
    await WaitUntilWithTimeoutAsync(condition, TimeSpan.FromSeconds(5), clients);
}

static async Task WaitUntilWithTimeoutAsync(Func<bool> condition, TimeSpan timeout, params MmoClient[] clients)
{
    var startedAt = DateTimeOffset.UtcNow;
    var deadline = startedAt + timeout;
    while (DateTimeOffset.UtcNow < deadline)
    {
        var elapsed = DateTimeOffset.UtcNow - startedAt;
        foreach (var client in clients)
        {
            client.Poll(elapsed);
        }

        if (condition())
        {
            return;
        }

        await Task.Delay(10);
    }

    throw new TimeoutException("Timed out waiting for movement debug trace harness condition.");
}

static async Task PumpForAsync(TimeSpan duration, params MmoClient[] clients)
{
    var startedAt = DateTimeOffset.UtcNow;
    while (DateTimeOffset.UtcNow - startedAt < duration)
    {
        var elapsed = DateTimeOffset.UtcNow - startedAt;
        foreach (var client in clients)
        {
            client.Poll(elapsed);
        }

        await Task.Delay(10);
    }
}

static string ReadString(string[] args, string prefix, string fallback)
{
    var match = args.FirstOrDefault(arg => arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    return match is null ? fallback : match[prefix.Length..];
}
'@

& $dotnet run --project $project -- "--root=$root"
