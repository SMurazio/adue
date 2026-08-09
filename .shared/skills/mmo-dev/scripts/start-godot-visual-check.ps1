param(
    [string]$FirstName = 'GodotA',
    [string]$SecondName = 'GodotB',
    [string]$HostName = '127.0.0.1',
    [int]$Port = 7777,
    [string]$ConnectionKey = 'local-dev',
    [string]$AdminNames = '',
    [int]$ControlPort = 7780,
    [ValidateRange(1, 2)]
    [int]$Clients = 1,
    [switch]$LogToFile,
    [string]$LogPath = '',
    [string]$ErrorLogPath = '',
    [switch]$NoStop,
    [switch]$SkipBuild,
    [switch]$SkipImport
)

$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..\..')).Path
$project = Join-Path $root 'src\Mmo.Client.Godot'
$runDir = Join-Path $root '.run'
$stopScript = Join-Path $PSScriptRoot 'stop-mmo.cmd'
$startServerScript = Join-Path $PSScriptRoot 'start-server.cmd'
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
  setx MMO_GODOT "D:\Tools\Godot\Godot_v4.6.3-stable_mono_win64.exe"
Open a new terminal afterwards, then retry.
"@
}

function Convert-ToSafePidName {
    param([string]$Value)
    return ($Value -replace '[^A-Za-z0-9_.-]', '_')
}

function Start-GodotClient {
    param(
        [string]$Name,
        [string]$Godot,
        [int]$Index
    )

    $safeName = Convert-ToSafePidName $Name
    $pidFile = Join-Path $runDir "godot-client-$safeName.pid"

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $Godot
    $psi.Arguments = "--path `"$project`""
    $psi.WorkingDirectory = $project
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $false
    $psi.EnvironmentVariables['MMO_HOST'] = $HostName
    $psi.EnvironmentVariables['MMO_PORT'] = [string]$Port
    $psi.EnvironmentVariables['MMO_CONNECTION_KEY'] = $ConnectionKey
    $psi.EnvironmentVariables['MMO_PLAYER_NAME'] = $Name
    $psi.EnvironmentVariables['MMO_GODOT_STARTUP_CHAT'] = "visual check client $Index online"

    # Open the debug control channel on the FIRST client only so the mmo-client-control MCP can drive it
    # (move/autopilot) and read interp/frame telemetry. Single port -> one controllable client; the second
    # client stays a plain remote-movement reference. ControlPort=0 disables it.
    if ($Index -eq 1 -and $ControlPort -gt 0) {
        $psi.EnvironmentVariables['MMO_DEBUG_CONTROL_PORT'] = [string]$ControlPort
    }

    $process = [System.Diagnostics.Process]::Start($psi)
    $process.Id | Set-Content -LiteralPath $pidFile
    "Godot client '$Name' started as PID $($process.Id)."
}

$godot = Resolve-Godot

if (-not $NoStop) {
    "Stopping existing MMO server/client windows first..."
    & $stopScript -SkipLauncherProcesses
}

"Starting MMO server..."
$effectiveAdminNames = if ([string]::IsNullOrWhiteSpace($AdminNames)) {
    "Admin,$FirstName,$SecondName"
} else {
    $AdminNames
}
$env:MMO_ADMIN_NAMES = $effectiveAdminNames
"Admin names for visual check: $effectiveAdminNames"

# ADUE P2 (todo/S-p2-auto-pair-and-duo-reveal.md): the two-player DEMO front door (start-duo -> -Clients 2) turns on
# server DEMO MODE via MMO_DEMO_MODE — auto-pair the two players on join (no typed /pair) and refuse an unpaired
# solo-start ("Waiting for your partner to join."). A SOLO visual check (-Clients 1) leaves it unset, so single-client
# dev keeps today's solo-start + /pair behaviour. The var is inherited by the server the start-server script launches.
if ($Clients -ge 2) {
    $env:MMO_DEMO_MODE = '1'
    "Demo mode ON (auto-pair + solo-start guard) for the two-player front door."
} else {
    Remove-Item Env:\MMO_DEMO_MODE -ErrorAction SilentlyContinue
}
$serverArgs = @()
if ($LogToFile) {
    $serverArgs += '-LogToFile'
}

if (-not [string]::IsNullOrWhiteSpace($LogPath)) {
    $serverArgs += @('-LogPath', $LogPath)
}

if (-not [string]::IsNullOrWhiteSpace($ErrorLogPath)) {
    $serverArgs += @('-ErrorLogPath', $ErrorLogPath)
}

& $startServerScript @serverArgs

if (-not $SkipBuild) {
    "Building Godot client C#..."
    & $godotBuildScript
}

# Headless asset import: Godot play mode does NOT import new/changed resources (that is an editor /
# `--headless --import` operation). Assets added without opening the editor (e.g. an agent copying PNGs)
# have no `.import` sidecar, so `GD.Load<Texture2D>(...)` returns null at runtime and sprites fall back to
# the box. This pass is incremental (cheap on warm projects). It runs even with -SkipBuild because the
# import gap is independent of the C# build. `--import` can exit non-zero even on a successful incremental
# import, so a non-zero exit is logged, not fatal -- wrapped here because $ErrorActionPreference = 'Stop'
# would otherwise abort the launch on the native exit code.
if (-not $SkipImport) {
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

Start-Sleep -Seconds 2

Start-GodotClient -Name $FirstName -Godot $godot -Index 1
if ($Clients -ge 2) {
    Start-Sleep -Milliseconds 500
    Start-GodotClient -Name $SecondName -Godot $godot -Index 2
}

"Visual check launched."
if ($ControlPort -gt 0) {
    "'$FirstName' has the debug control channel on port $ControlPort -> the mmo-client-control MCP can drive it."
}
if ($Clients -ge 2) {
    "Verify: map renders, '$FirstName' and '$SecondName' are both visible, WASD/diagonals glide, remote movement updates."
} else {
    "Verify: map renders, '$FirstName' is visible, WASD/diagonals glide."
}
"Stop everything with: .\.shared\skills\mmo-dev\scripts\stop-mmo.cmd"
