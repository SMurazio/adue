# DEVX1 — make dev scripts fall back to a global `dotnet` (collaborator clone-and-go)

PRODUCTION on `main`. The workspace is being shared with a collaborator. The `.shared/skills/mmo-dev/scripts/*.ps1`
scripts hardcode the repo-local SDK at `.tools\dotnet\dotnet.exe`, but `.tools/` is **gitignored** — so a fresh
clone doesn't have it and the scripts fail. Fix: each script that invokes dotnet should **prefer the repo-local
SDK if present, else fall back to a globally-installed `dotnet`** (on PATH). This makes `run-checks.cmd`,
`godot-build.cmd`, `start-server.cmd`, etc. work for anyone with .NET 8 installed.

## Scripts that reference `.tools\dotnet` (9)
`godot-build.ps1`, `movement-debug-trace.ps1`, `review-stress.ps1`, `run-checks.ps1`, `run-server-window.ps1`,
`start-server.ps1`, `start-web-client.ps1`, `stress-test.ps1`, and `stop-mmo.ps1`.

## What to do
- In each script that **invokes** dotnet, replace the hardcoded resolution
  (`$dotnet = Join-Path $root '.tools\dotnet\dotnet.exe'` — note the root var is `$root` in some, `$repo` in
  others) with a fallback, e.g.:
  ```powershell
  $localDotnet = Join-Path $root '.tools\dotnet\dotnet.exe'
  $dotnet = if (Test-Path $localDotnet) { $localDotnet } else { 'dotnet' }
  ```
  Keep preferring the local SDK when present (parity with the maintainer's pinned 8.0.422). Use the script's
  existing root variable name; don't rename it.
- **`stop-mmo.ps1`:** it likely references `.tools/dotnet` only to identify/kill processes by path, not to invoke
  dotnet — if so, leave its process-matching logic alone (it must still stop `.tools/dotnet`-launched servers);
  only change ACTUAL dotnet invocations. Check before editing.
- If a globally-installed `dotnet` is missing AND there's no local SDK, a clear error ("`.NET 8 SDK not found —
  install it or set up .tools/dotnet`") is nicer than a raw command-not-found, but optional.
- The Godot-binary scripts (`godot-run.ps1`, `godot-import.ps1`, `start-godot-visual-check.ps1`) already resolve
  Godot via PATH/`MMO_GODOT` — **do not touch those**.

## Gates
- The scripts must still work **with `.tools/dotnet` present** (this machine has it → the fallback resolves to the
  local SDK, behavior unchanged): the Orchestrator will run `run-checks.cmd` + `godot-build.cmd` to confirm. The
  fallback-to-global path can't be tested here (the local SDK is present) — verify that branch by reading.
- **Do NOT run `stop-mmo`/kill a live session.** If shell/git denied, leave work + `review/review-request-devx1.md`.

## Standing rules
One discrete revertable commit referencing this task; delete the todo in it. **Safe Local Execution**.

## Acceptance
Every dotnet-invoking dev script resolves dotnet as "local `.tools/dotnet` if present, else global `dotnet`";
`run-checks.cmd` + `godot-build.cmd` still pass on this machine; the change is read-verified for the
global-fallback branch. A fresh clone with only a global .NET 8 SDK can run the scripts.
