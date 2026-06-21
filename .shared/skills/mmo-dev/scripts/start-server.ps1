param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [switch]$LogToFile,
    [string]$LogPath = '',
    [string]$ErrorLogPath = ''
)

$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..\..')).Path
$localDotnet = Join-Path $root '.tools\dotnet\dotnet.exe'
$dotnet = if (Test-Path $localDotnet) { $localDotnet } else { 'dotnet' }
$runDir = Join-Path $root '.run'
$pidFile = Join-Path $runDir 'server.pid'
$windowScript = Join-Path $PSScriptRoot 'run-server-window.ps1'

New-Item -ItemType Directory -Force -Path $runDir | Out-Null

if (Test-Path -LiteralPath $pidFile) {
    $existingPid = Get-Content -LiteralPath $pidFile -Raw
    $existing = Get-Process -Id ([int]$existingPid) -ErrorAction SilentlyContinue
    if ($existing) {
        "Server already running as PID $existingPid"
        exit 0
    }
}

& $dotnet build (Join-Path $root 'src\Mmo.Server\Mmo.Server.csproj') --no-restore -c $Configuration

$serverDll = Join-Path $root "src\Mmo.Server\bin\$Configuration\net8.0\Mmo.Server.dll"
$effectiveLogPath = ''
$effectiveErrorLogPath = ''
if ($LogToFile -or -not [string]::IsNullOrWhiteSpace($LogPath) -or -not [string]::IsNullOrWhiteSpace($ErrorLogPath)) {
    $effectiveLogPath = if ([string]::IsNullOrWhiteSpace($LogPath)) {
        Join-Path $runDir 'server.log'
    } else {
        $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($LogPath)
    }

    $effectiveErrorLogPath = if ([string]::IsNullOrWhiteSpace($ErrorLogPath)) {
        Join-Path $runDir 'server.err.log'
    } else {
        $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ErrorLogPath)
    }

    foreach ($path in @($effectiveLogPath, $effectiveErrorLogPath)) {
        $directory = Split-Path -Parent $path
        if (-not [string]::IsNullOrWhiteSpace($directory)) {
            New-Item -ItemType Directory -Force -Path $directory | Out-Null
        }

        if (-not (Test-Path -LiteralPath $path)) {
            New-Item -ItemType File -Path $path | Out-Null
        }
    }
}

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = 'powershell.exe'
$psi.Arguments = "-NoProfile -File `"$windowScript`" -ServerDll `"$serverDll`" -Root `"$root`" -LogPath `"$effectiveLogPath`" -ErrorLogPath `"$effectiveErrorLogPath`""
$psi.WorkingDirectory = $root
$psi.UseShellExecute = $true
$psi.WindowStyle = [System.Diagnostics.ProcessWindowStyle]::Normal

$process = [System.Diagnostics.Process]::Start($psi)

$process.Id | Set-Content -LiteralPath $pidFile
if ([string]::IsNullOrWhiteSpace($effectiveLogPath)) {
    "Server started as PID $($process.Id) ($Configuration)"
} else {
    "Server started as PID $($process.Id) ($Configuration); logging to $effectiveLogPath and $effectiveErrorLogPath"
}
