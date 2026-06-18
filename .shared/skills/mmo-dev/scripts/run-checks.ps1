$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..\..')).Path
$dotnet = Join-Path $root '.tools\dotnet\dotnet.exe'

& $dotnet build (Join-Path $root 'Mmo.sln') --no-restore
& $dotnet test (Join-Path $root 'Mmo.sln') --no-build --no-restore
