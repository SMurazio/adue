param(
    [string]$FirstName = 'GodotA',
    [string]$SecondName = 'GodotB',
    [string]$HostName = '127.0.0.1',
    [int]$Port = 7777,
    [string]$ConnectionKey = 'local-dev',
    [string]$AdminNames = '',
    [switch]$NoStop,
    [switch]$SkipBuild
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
& $startServerScript

if (-not $SkipBuild) {
    "Building Godot client C#..."
    & $godotBuildScript
}

Start-Sleep -Seconds 2

Start-GodotClient -Name $FirstName -Godot $godot -Index 1
Start-Sleep -Milliseconds 500
Start-GodotClient -Name $SecondName -Godot $godot -Index 2

"Visual check launched."
"Verify: map renders, '$FirstName' and '$SecondName' are both visible, WASD/diagonals glide, remote movement updates."
"Stop everything with: .\.shared\skills\mmo-dev\scripts\stop-mmo.cmd"
