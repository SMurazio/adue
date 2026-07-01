$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..\..')).Path
$localDotnet = Join-Path $root '.tools\dotnet\dotnet.exe'
$dotnet = if (Test-Path $localDotnet) { $localDotnet } else { 'dotnet' }

# --no-incremental forces a full recompile so a stale fast-up-to-date-check can't reuse a test
# assembly compiled against an old signature (which would silently run stale tests and hide a real
# build break — exactly what happened with the S98 TryStep signature change). The test step then
# runs the freshly-built assemblies with --no-build.
& $dotnet build (Join-Path $root 'Mmo.sln') --no-restore --no-incremental
# Category!=Measure excludes the QUARANTINED non-asserting measurement harnesses (CombatLagMeasure,
# MonsterPerfMeasure, the 2 DtBudget probes) from the default gate — they can't regress and burn ~30s+. Run them
# on demand with: dotnet test --filter "Category=Measure".
& $dotnet test (Join-Path $root 'Mmo.sln') --no-build --no-restore --filter "Category!=Measure"
