param(
    [int]$Clients = 120,
    [string]$Duration = '60s',
    [int]$MetricsDelaySeconds = 0,
    [string]$HostName = '127.0.0.1',
    [int]$Port = 7777,
    [string]$ConnectionKey = 'local-dev',
    [switch]$KeepServer
)

$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..\..')).Path
$dotnet = Join-Path $root '.tools\dotnet\dotnet.exe'
$stopScript = Join-Path $PSScriptRoot 'stop-mmo.cmd'
$startServerScript = Join-Path $PSScriptRoot 'start-server.cmd'
$stressScript = Join-Path $PSScriptRoot 'stress-test.cmd'
$metricsProject = Join-Path $root '.run\MetricsClient\MetricsClient.csproj'

function Convert-DurationToSeconds {
    param([string]$Value)

    $trimmed = $Value.Trim()
    if ($trimmed.EndsWith('ms', [StringComparison]::OrdinalIgnoreCase)) {
        return [Math]::Max(1, [int][Math]::Ceiling([double]$trimmed.Substring(0, $trimmed.Length - 2) / 1000.0))
    }

    if ($trimmed.EndsWith('s', [StringComparison]::OrdinalIgnoreCase)) {
        return [Math]::Max(1, [int][Math]::Ceiling([double]$trimmed.Substring(0, $trimmed.Length - 1)))
    }

    if ($trimmed.EndsWith('m', [StringComparison]::OrdinalIgnoreCase)) {
        return [Math]::Max(1, [int][Math]::Ceiling([double]$trimmed.Substring(0, $trimmed.Length - 1) * 60.0))
    }

    $timeSpan = [TimeSpan]::Zero
    if ([TimeSpan]::TryParse($trimmed, [ref]$timeSpan)) {
        return [Math]::Max(1, [int][Math]::Ceiling($timeSpan.TotalSeconds))
    }

    return [Math]::Max(1, [int][Math]::Ceiling([double]$trimmed))
}

function Ensure-MetricsClient {
    $projectDir = Split-Path -Parent $metricsProject
    New-Item -ItemType Directory -Force -Path $projectDir | Out-Null

    Set-Content -LiteralPath $metricsProject -Encoding ASCII -Value @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <UseAppHost>false</UseAppHost>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="LiteNetLib" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\Mmo.Shared\Mmo.Shared.csproj" />
  </ItemGroup>
</Project>
'@

    Set-Content -LiteralPath (Join-Path $projectDir 'Program.cs') -Encoding ASCII -Value @'
using LiteNetLib;
using Mmo.Shared.Protocol;

var host = ReadString(args, "--host=", "127.0.0.1");
var port = ReadInt(args, "--port=", 7777);
var key = ReadString(args, "--key=", "local-dev");
var name = ReadString(args, "--name=", "Admin");
var listener = new EventBasedNetListener();
var client = new NetManager(listener) { AutoRecycle = false };
var metrics = new List<string>();
NetPeer? serverPeer = null;
var sentMetrics = false;

listener.PeerConnectedEvent += peer =>
{
    serverPeer = peer;
    Send(new ClientHelloMessage("metrics-client"), DeliveryMethod.ReliableOrdered);
    Send(new LoginRequestMessage(name, name), DeliveryMethod.ReliableOrdered);
};

listener.NetworkReceiveEvent += (_, reader, _, _) =>
{
    try
    {
        var message = ProtocolCodec.Decode(reader.GetRemainingBytes());
        switch (message)
        {
            case WorldSnapshotMessage snapshot:
                Send(new SnapshotAckMessage(snapshot.SnapshotSequence), DeliveryMethod.Sequenced);
                break;
            case LoginResultMessage login when login.Accepted && !sentMetrics:
                sentMetrics = true;
                Send(new ChatSendMessage("/metrics"), DeliveryMethod.ReliableOrdered);
                break;
            case ChatBroadcastMessage chat when chat.Sender == "server":
                metrics.Add(chat.Text);
                Console.WriteLine(chat.Text);
                break;
        }
    }
    finally
    {
        reader.Recycle();
    }
};

client.Start();
client.Connect(host, port, key);

var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(8);
while (DateTimeOffset.UtcNow < deadline && metrics.Count < 5)
{
    client.PollEvents();
    Thread.Sleep(10);
}

client.Stop();
return metrics.Count >= 4 ? 0 : 1;

void Send(IProtocolMessage message, DeliveryMethod deliveryMethod)
{
    serverPeer?.Send(ProtocolCodec.Encode(message), 0, deliveryMethod);
}

static string ReadString(string[] args, string prefix, string fallback)
{
    var match = args.FirstOrDefault(arg => arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    return match is null ? fallback : match[prefix.Length..];
}

static int ReadInt(string[] args, string prefix, int fallback)
{
    var value = ReadString(args, prefix, "");
    return int.TryParse(value, out var parsed) ? parsed : fallback;
}
'@

    & $dotnet build $metricsProject
}

$durationSeconds = Convert-DurationToSeconds $Duration
if ($MetricsDelaySeconds -le 0) {
    $MetricsDelaySeconds = [Math]::Max(1, $durationSeconds - 8)
}

$MetricsDelaySeconds = [Math]::Min($MetricsDelaySeconds, [Math]::Max(1, $durationSeconds - 1))
Ensure-MetricsClient

Write-Output "Review stress: clients=$Clients duration=$Duration metricsDelay=${MetricsDelaySeconds}s"
Write-Output "--- STOP EXISTING SERVER ---"
& $stopScript

try {
    Write-Output "--- START SERVER ---"
    & $startServerScript
    Start-Sleep -Seconds 2

    $stressJob = Start-Job -ScriptBlock {
        param($Root, $StressScript, $Clients, $Duration)
        Set-Location $Root
        & $StressScript "--clients=$Clients" "--duration=$Duration"
        if ($LASTEXITCODE -ne 0) {
            throw "stress-test exited with code $LASTEXITCODE"
        }
    } -ArgumentList $root, $stressScript, $Clients, $Duration

    Start-Sleep -Seconds $MetricsDelaySeconds
    Write-Output "--- METRICS DURING STRESS ---"
    & $dotnet run --no-build --project $metricsProject -- "--host=$HostName" "--port=$Port" "--key=$ConnectionKey" "--name=Admin"

    Wait-Job $stressJob | Out-Null
    Write-Output "--- STRESS OUTPUT ---"
    Receive-Job $stressJob

    if ($stressJob.State -ne 'Completed') {
        throw "Stress job ended with state $($stressJob.State)."
    }
}
finally {
    if (-not $KeepServer) {
        Write-Output "--- STOP SERVER ---"
        & $stopScript
    }
}
