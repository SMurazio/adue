#requires -Version 5
# Headlessly import the Godot project's assets so newly-added content (e.g. .glb models dropped into
# content/) gets its .import metadata + import cache generated WITHOUT opening the editor GUI. Same Godot
# binary the visual check uses (MMO_GODOT). Visible, script-based, no hidden window / bypass / PID-kill.
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..\..')).Path
$project = Join-Path $root 'src\Mmo.Client.Godot'

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

$godot = Resolve-Godot
Write-Host "Importing Godot assets in $project"
Write-Host "Using Godot: $godot"
# --headless: no GPU/window; --import: (re)import all resources, generating .import files + the import cache,
# then exit. --quit-after bounds the run so a stuck import can't hang the session.
& $godot --headless --path "$project" --import --quit-after 600
exit $LASTEXITCODE
