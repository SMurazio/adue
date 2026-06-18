$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..\..')).Path
$dotnet = Join-Path $root '.tools\dotnet\dotnet.exe'
$project = Join-Path $root 'src\Mmo.Tools.Stress\Mmo.Tools.Stress.csproj'

$stressArgs = @($args)
if ($stressArgs.Count -eq 0) {
    $stressArgs = @('--clients=25', '--duration=20s')
}

& $dotnet build $project --no-restore
& $dotnet run --no-build --project $project -- $stressArgs
