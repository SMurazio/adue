param(
    [switch]$DryRun,
    [switch]$SkipLauncherProcesses
)

$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..\..')).Path
$runDir = Join-Path $root '.run'
$pidFiles = @(
    (Join-Path $runDir 'server.pid'),
    (Join-Path $runDir 'web-client.pid')
)
if (Test-Path -LiteralPath $runDir) {
    $pidFiles += Get-ChildItem -LiteralPath $runDir -Filter 'godot-client*.pid' -File -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty FullName
}
$stopped = New-Object 'System.Collections.Generic.HashSet[int]'

function Test-ContainsAny {
    param(
        [string]$Value,
        [string[]]$Needles
    )

    foreach ($needle in $Needles) {
        if ($Value.IndexOf($needle, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            return $true
        }
    }

    return $false
}

function Stop-MmoProcess {
    param(
        [int]$ProcessId,
        [string]$Reason
    )

    if ($ProcessId -le 0 -or $ProcessId -eq $PID) {
        return
    }

    if (-not $script:stopped.Add($ProcessId)) {
        return
    }

    $process = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
    if (-not $process) {
        return
    }

    if ($script:DryRun) {
        "Would stop PID $($process.Id) ($($process.ProcessName)): $Reason"
        return
    }

    Stop-Process -Id $process.Id -Force
    "Stopped PID $($process.Id) ($($process.ProcessName)): $Reason"
}

foreach ($pidFile in $pidFiles) {
    if (-not (Test-Path -LiteralPath $pidFile)) {
        continue
    }

    $pidText = Get-Content -LiteralPath $pidFile -Raw
    $parsedPid = 0
    if ([int]::TryParse($pidText.Trim(), [ref]$parsedPid)) {
        Stop-MmoProcess $parsedPid "pid file $pidFile"
    }

    if (-not $DryRun) {
        Remove-Item -LiteralPath $pidFile -Force
    }
}

$portPids = @()
foreach ($port in @(7777, 5080)) {
    $lines = & netstat -ano | Select-String -Pattern ":$port\s"
    foreach ($line in $lines) {
        $parts = ($line.Line -split '\s+') | Where-Object { $_ }
        if ($parts.Count -lt 3 -or -not $parts[1].EndsWith(":$port", [StringComparison]::OrdinalIgnoreCase)) {
            continue
        }

        $parsedPid = 0
        if ($parts.Count -gt 0 -and [int]::TryParse($parts[-1], [ref]$parsedPid)) {
            $portPids += $parsedPid
        }
    }
}

foreach ($processId in ($portPids | Sort-Object -Unique)) {
    Stop-MmoProcess $processId "listener on MMO port"
}

$repoDotnet = Join-Path $root '.tools\dotnet\dotnet.exe'
foreach ($process in (Get-Process dotnet -ErrorAction SilentlyContinue)) {
    if ($process.Path -eq $repoDotnet) {
        Stop-MmoProcess $process.Id "repo-local dotnet runtime"
    }
}

$windowTitlePrefixes = @(
    'MMO Server',
    'MMO Web',
    'MMO Web Client'
)

foreach ($process in (Get-Process -ErrorAction SilentlyContinue)) {
    if ([string]::IsNullOrWhiteSpace($process.MainWindowTitle)) {
        continue
    }

    foreach ($prefix in $windowTitlePrefixes) {
        if ($process.MainWindowTitle.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
            Stop-MmoProcess $process.Id "MMO window title '$($process.MainWindowTitle)'"
            break
        }
    }
}

$repoMarkers = @(
    $root,
    (Join-Path $root '.tools\dotnet\dotnet.exe'),
    $PSScriptRoot
)

$runtimeMarkers = @(
    'Mmo.Server.csproj',
    'Mmo.Client.Web.csproj',
    'Mmo.Server.dll',
    'Mmo.Client.Web.dll',
    'Mmo.Client.Godot',
    'MmoClientGodot',
    'run-server-window.cmd',
    'run-web-client-window.cmd',
    'start-server.ps1',
    'start-web-client.ps1',
    'start-server.cmd',
    'start-web-client.cmd'
)
if (-not $SkipLauncherProcesses) {
    $runtimeMarkers += @(
        'start-godot-visual-check.ps1',
        'start-godot-visual-check.cmd'
    )
}

try {
    $processes = Get-CimInstance Win32_Process -ErrorAction Stop
    foreach ($process in $processes) {
        $commandLine = $process.CommandLine
        if ([string]::IsNullOrWhiteSpace($commandLine)) {
            continue
        }

        if ((Test-ContainsAny $commandLine $repoMarkers) -and (Test-ContainsAny $commandLine $runtimeMarkers)) {
            Stop-MmoProcess ([int]$process.ProcessId) "MMO runtime command line"
        }
    }
}
catch {
    "Could not inspect command lines for leftover wrapper windows: $($_.Exception.Message)"
}
