#requires -Version 5
# Compile the Godot client's C# (no editor, no Godot launch) using the repo-local SDK.
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..\..')).Path
$localDotnet = Join-Path $repo '.tools\dotnet\dotnet.exe'
$dotnet = if (Test-Path $localDotnet) { $localDotnet } else { 'dotnet' }
$sln = Join-Path $repo 'src\Mmo.Client.Godot\MmoClientGodot.sln'

if (-not (Test-Path $sln)) {
    Write-Host "Godot solution not found at: $sln"
    Write-Host "Open the Godot project once (that generates the .sln/.csproj), then retry."
    exit 1
}

Write-Host "Building Godot client: $sln"
& $dotnet build $sln -v minimal
exit $LASTEXITCODE
