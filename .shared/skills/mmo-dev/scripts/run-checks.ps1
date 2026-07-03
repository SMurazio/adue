$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..\..')).Path
$localDotnet = Join-Path $root '.tools\dotnet\dotnet.exe'
$dotnet = if (Test-Path $localDotnet) { $localDotnet } else { 'dotnet' }

# --no-incremental forces a full recompile so a stale fast-up-to-date-check can't reuse a test
# assembly compiled against an old signature (which would silently run stale tests and hide a real
# build break — exactly what happened with the S98 TryStep signature change). The test step then
# runs the freshly-built assemblies with --no-build.
& $dotnet build (Join-Path $root 'Mmo.sln') --no-restore --no-incremental
# GATE INTEGRITY (todo S-run-checks-false-green-on-build-failure): a native command's failure does NOT
# stop PowerShell ($ErrorActionPreference only covers cmdlets), and the script's exit code used to be
# whatever the LAST command returned — so a FAILED build fell through to `dotnet test`, which ran only
# the stale DLLs that happened to exist and exited 0. That false-greened the whole verification gate
# (caught live: a CS0103 in the T1 telegraph tree "passed"). Check $LASTEXITCODE after EVERY dotnet
# call and stop at the first failure.
if ($LASTEXITCODE -ne 0) {
    Write-Host "run-checks: BUILD FAILED (exit $LASTEXITCODE) - not running tests." -ForegroundColor Red
    exit $LASTEXITCODE
}
# Category!=Measure excludes the QUARANTINED non-asserting measurement harnesses (CombatLagMeasure,
# MonsterPerfMeasure, the 2 DtBudget probes) from the default gate — they can't regress and burn ~30s+. Run them
# on demand with: dotnet test --filter "Category=Measure".
# Tee the output so we can ALSO fail on VSTest's "provided was not found" — it reports a missing test
# source (build/test drift: a suite's DLL absent means that suite silently didn't run) yet can still
# exit 0 when the suites it DID find all pass. (No 2>&1: PS5.1 wraps native stderr in ErrorRecords.)
& $dotnet test (Join-Path $root 'Mmo.sln') --no-build --no-restore --filter "Category!=Measure" | Tee-Object -Variable testOutput
if ($LASTEXITCODE -ne 0) {
    Write-Host "run-checks: TESTS FAILED (exit $LASTEXITCODE)." -ForegroundColor Red
    exit $LASTEXITCODE
}
if ($testOutput -match 'provided was not found') {
    Write-Host "run-checks: a test source DLL was NOT FOUND - a suite silently did not run. Failing the gate." -ForegroundColor Red
    exit 1
}
exit 0
