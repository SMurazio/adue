#requires -Version 5
# Run the Godot client headless for a few seconds and capture its output (compile + runtime smoke).
# Usage: godot-run.cmd [seconds]   (default 8). Set MMO_GODOT to your Godot .NET exe first.
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..\..')).Path
$project = Join-Path $repo 'src\Mmo.Client.Godot'

# Resolve the Godot executable: MMO_GODOT env var, else 'godot' on PATH.
$godot = $env:MMO_GODOT
if ([string]::IsNullOrWhiteSpace($godot) -or -not (Test-Path $godot)) {
    $cmd = Get-Command godot -ErrorAction SilentlyContinue
    if ($cmd) { $godot = $cmd.Source }
}
if ([string]::IsNullOrWhiteSpace($godot) -or -not (Test-Path $godot)) {
    Write-Host "Godot executable not found."
    Write-Host "Set MMO_GODOT to your Godot .NET exe (persists for your account):"
    Write-Host '  setx MMO_GODOT "D:\Tools\Godot\Godot_v4.6.3-stable_mono_win64.exe"'
    Write-Host "Open a new terminal afterwards, then retry. (Or add godot to PATH.)"
    exit 1
}

$seconds = 8
if ($args.Count -ge 1) { $seconds = [int]$args[0] }

$outFile = [System.IO.Path]::GetTempFileName()
$errFile = [System.IO.Path]::GetTempFileName()
Write-Host "Running Godot headless for ~$seconds s: $project"
Write-Host "(for a server-connection smoke, start the MMO server first via start-server.cmd)"
$proc = Start-Process -FilePath $godot `
    -ArgumentList @('--headless', '--path', $project, '--build-solutions') `
    -PassThru -NoNewWindow -RedirectStandardOutput $outFile -RedirectStandardError $errFile
try {
    if (-not $proc.WaitForExit($seconds * 1000)) {
        $proc.Kill(); $proc.WaitForExit()
        Write-Host "(stopped after ~$seconds s)"
    } else {
        Write-Host "(exited on its own, code $($proc.ExitCode))"
    }
} finally {
    Write-Host "----- stdout -----"
    Get-Content $outFile -ErrorAction SilentlyContinue
    Write-Host "----- stderr -----"
    Get-Content $errFile -ErrorAction SilentlyContinue
    Remove-Item $outFile, $errFile -ErrorAction SilentlyContinue
}
