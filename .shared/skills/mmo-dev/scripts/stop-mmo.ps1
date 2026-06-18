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

# NOTE (fix): a blind `Get-CimInstance Win32_Process` command-line sweep used to run here to catch
# leftover wrapper processes. On managed/EDR Windows it hung (WMI process enumeration with
# CommandLine is intercepted and can stall indefinitely) and risked force-killing unrelated
# processes that merely referenced the repo path (e.g. an open editor or terminal). It is removed:
# the tracked pid files (server/web-client/godot-client*), MMO port listeners, repo-local dotnet
# processes, and the MMO window-title scan above already cover server + client shutdown. The
# $SkipLauncherProcesses switch is retained for caller compatibility but is now a no-op.
