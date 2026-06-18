$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..\..')).Path
$dotnet = Join-Path $root '.tools\dotnet\dotnet.exe'
$runDir = Join-Path $root '.run'
$pidFile = Join-Path $runDir 'server.pid'

New-Item -ItemType Directory -Force -Path $runDir | Out-Null

if (Test-Path -LiteralPath $pidFile) {
    $existingPid = Get-Content -LiteralPath $pidFile -Raw
    $existing = Get-Process -Id ([int]$existingPid) -ErrorAction SilentlyContinue
    if ($existing) {
        "Server already running as PID $existingPid"
        exit 0
    }
}

& $dotnet build (Join-Path $root 'src\Mmo.Server\Mmo.Server.csproj') --no-restore

$serverDll = Join-Path $root 'src\Mmo.Server\bin\Debug\net8.0\Mmo.Server.dll'

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $dotnet
$psi.Arguments = "`"$serverDll`""
$psi.WorkingDirectory = $root
$psi.UseShellExecute = $true
$psi.WindowStyle = [System.Diagnostics.ProcessWindowStyle]::Normal

$process = [System.Diagnostics.Process]::Start($psi)

$process.Id | Set-Content -LiteralPath $pidFile
"Server started as PID $($process.Id)"
