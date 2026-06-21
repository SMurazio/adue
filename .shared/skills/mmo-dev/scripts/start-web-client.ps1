$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..\..')).Path
$localDotnet = Join-Path $root '.tools\dotnet\dotnet.exe'
$dotnet = if (Test-Path $localDotnet) { $localDotnet } else { 'dotnet' }
$runDir = Join-Path $root '.run'
$pidFile = Join-Path $runDir 'web-client.pid'

New-Item -ItemType Directory -Force -Path $runDir | Out-Null

if (Test-Path -LiteralPath $pidFile) {
    $existingPid = Get-Content -LiteralPath $pidFile -Raw
    $existing = Get-Process -Id ([int]$existingPid) -ErrorAction SilentlyContinue
    if ($existing) {
        "Web client already running as PID $existingPid at http://127.0.0.1:5080"
        exit 0
    }
}

& $dotnet build (Join-Path $root 'src\Mmo.Client.Web\Mmo.Client.Web.csproj') --no-restore

$webDll = Join-Path $root 'src\Mmo.Client.Web\bin\Debug\net8.0\Mmo.Client.Web.dll'

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $dotnet
$psi.Arguments = "`"$webDll`""
$psi.WorkingDirectory = Join-Path $root 'src\Mmo.Client.Web'
$psi.UseShellExecute = $true
$psi.WindowStyle = [System.Diagnostics.ProcessWindowStyle]::Normal

$process = [System.Diagnostics.Process]::Start($psi)

$process.Id | Set-Content -LiteralPath $pidFile
"Web client started as PID $($process.Id). Open http://127.0.0.1:5080"
