# connect-server.ps1 — JOIN a remote MMO server (LAN side-by-side play). Launches ONE Godot client pointed at a
# remote host, with NO local server. This is the counterpart to start-godot-visual-check (which HOSTS locally):
# the HOST runs their server normally; the JOINER runs THIS with the host's LAN IP.
#
# Usage (the joiner runs this):
#   .\.shared\skills\mmo-dev\scripts\connect-server.cmd                       # joins the default host below
#   .\.shared\skills\mmo-dev\scripts\connect-server.cmd -Server 192.168.1.42  # join a different host
#   .\.shared\skills\mmo-dev\scripts\connect-server.cmd -Name Bob             # pick your in-game name
#
# Requires: same LAN as the host; the host's server running; the host opened inbound UDP <Port> in their
# firewall; MMO_GODOT set to the Godot .NET exe.
param(
    [string]$Server = '192.168.50.4',     # the HOST's LAN IP (current default). Override with -Server or edit this.
    [string]$Name = 'Guest',
    [int]$Port = 7777,
    [string]$ConnectionKey = 'local-dev', # MUST match the host server's connection key (this is the shared default).
    [switch]$SkipBuild,
    [switch]$SkipImport
)

$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..\..')).Path
$project = Join-Path $root 'src\Mmo.Client.Godot'
$runDir = Join-Path $root '.run'
$godotBuildScript = Join-Path $PSScriptRoot 'godot-build.cmd'

New-Item -ItemType Directory -Force -Path $runDir | Out-Null

function Resolve-Godot {
    $candidates = @(
        $env:MMO_GODOT,
        [Environment]::GetEnvironmentVariable('MMO_GODOT', 'User'),
        [Environment]::GetEnvironmentVariable('MMO_GODOT', 'Machine')
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    $cmd = Get-Command godot -ErrorAction SilentlyContinue
    if ($cmd) {
        return $cmd.Source
    }

    throw @"
Godot executable not found.
Set MMO_GODOT to your Godot .NET exe:
  setx MMO_GODOT "D:\Tools\Godot\Godot_v4.7-stable_mono_win64.exe"
Open a new terminal afterwards, then retry.
"@
}

$godot = Resolve-Godot

if (-not $SkipBuild) {
    "Building Godot client C#..."
    & $godotBuildScript
}

if (-not $SkipImport) {
    # Headless asset import (incremental; cheap on a warm project). Non-zero exit is non-fatal.
    "Importing Godot assets (headless)..."
    try {
        & $godot --headless --import --path $project
        if ($LASTEXITCODE -ne 0) {
            "Godot --import exited with code $LASTEXITCODE (non-fatal; continuing to launch)."
        }
    } catch {
        "Godot --import failed: $($_.Exception.Message) (non-fatal; continuing to launch)."
    }
}

"Connecting to MMO server at ${Server}:${Port} as '$Name'..."

$safeName = ($Name -replace '[^A-Za-z0-9_.-]', '_')
$pidFile = Join-Path $runDir "godot-client-$safeName.pid"

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $godot
$psi.Arguments = "--path `"$project`""
$psi.WorkingDirectory = $project
$psi.UseShellExecute = $false
$psi.CreateNoWindow = $false
$psi.EnvironmentVariables['MMO_HOST'] = $Server
$psi.EnvironmentVariables['MMO_PORT'] = [string]$Port
$psi.EnvironmentVariables['MMO_CONNECTION_KEY'] = $ConnectionKey
$psi.EnvironmentVariables['MMO_PLAYER_NAME'] = $Name

$process = [System.Diagnostics.Process]::Start($psi)
$process.Id | Set-Content -LiteralPath $pidFile
"Godot client '$Name' started as PID $($process.Id) -> ${Server}:${Port}."
"If it can't connect: confirm the host's server is running, you're on the same LAN, and the host opened inbound UDP $Port in their firewall."
"Stop it with: .\.shared\skills\mmo-dev\scripts\stop-mmo.cmd"
