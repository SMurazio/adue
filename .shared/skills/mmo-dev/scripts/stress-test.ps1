$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..\..')).Path
$dotnet = Join-Path $root '.tools\dotnet\dotnet.exe'
$project = Join-Path $root 'src\Mmo.Tools.Stress\Mmo.Tools.Stress.csproj'

$configuration = 'Debug'
$stressArgs = New-Object 'System.Collections.Generic.List[string]'
foreach ($arg in $args) {
    if ($arg -eq '--release') {
        $configuration = 'Release'
        continue
    }

    if ($arg.StartsWith('--configuration=', [StringComparison]::OrdinalIgnoreCase)) {
        $configuration = $arg.Substring('--configuration='.Length)
        continue
    }

    $stressArgs.Add($arg)
}

if ($configuration -ne 'Debug' -and $configuration -ne 'Release') {
    throw "Unsupported configuration '$configuration'. Use Debug or Release."
}

if ($stressArgs.Count -eq 0) {
    $stressArgs.Add('--clients=25')
    $stressArgs.Add('--duration=20s')
}

$stressArgsArray = $stressArgs.ToArray()
& $dotnet build $project --no-restore -c $configuration
& $dotnet run --no-build -c $configuration --project $project -- $stressArgsArray
