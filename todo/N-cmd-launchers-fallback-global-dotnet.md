# N — two `.cmd` launchers still hardcode `.tools\dotnet` (fresh-clone breakage)

Follow-up discovered while doing `DEVX1` (which scoped only the 9 `.ps1` scripts). Two `.cmd` launchers
referenced from the top-level `README.md` (lines 177-178, manual server/web-client launch path) still
hardcode the repo-local SDK and will fail on a fresh clone where `.tools/` is gitignored:

- `.shared/skills/mmo-dev/scripts/run-server-window.cmd:4`
- `.shared/skills/mmo-dev/scripts/run-web-client-window.cmd:4`

Both invoke `".tools\dotnet\dotnet.exe" run ...` directly.

## Why not fixed in DEVX1
DEVX1's title, scope list ("9 `.ps1` scripts"), and acceptance criteria all named only the `.ps1`
scripts. Per the standing rule (no silent scope expansion), this was split out instead of bundled.

## What to do
Make each `.cmd` prefer the repo-local SDK if present, else fall back to a global `dotnet` on PATH.
A batch equivalent of the PS fallback, e.g.:

```bat
@echo off
title MMO Server
cd /d "%~dp0..\..\..\.."
set "DOTNET=.tools\dotnet\dotnet.exe"
if not exist "%DOTNET%" set "DOTNET=dotnet"
"%DOTNET%" run --no-build --no-restore --project "src\Mmo.Server\Mmo.Server.csproj"
```

(and the analogous change for `run-web-client-window.cmd`, pointing at `src\Mmo.Client.Web\Mmo.Client.Web.csproj`).

## Acceptance
Both `.cmd` launchers resolve dotnet as "local `.tools/dotnet` if present, else global `dotnet`"; they
still work on this machine (local SDK present); read-verified for the global-fallback branch.
