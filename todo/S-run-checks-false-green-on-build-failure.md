# S — run-checks.cmd exits 0 on a FAILED build (false-green gate)

Observed 2026-07-03 on feat/continuous-migration: the T1 telegraph tree had a compile error
(`PlayerDamageGate.cs: CS0103 EntityKind`) — `dotnet build` printed `Build FAILED, 1 Error(s)` — yet
`.\.shared\skills\mmo-dev\scripts\run-checks.cmd` **exited 0**. `dotnet test` then ran against whatever
DLLs already existed: Mmo.Server.Tests.dll and Mmo.Client.Core.Tests.dll were "not found" (build failed
before producing them), only stale Mmo.Shared.Tests ran (157 passed), and VSTest apparently doesn't fail
the run for a missing test source here.

This false-greens EVERY task: the orchestrator's single verification gate can pass while the tree doesn't
even compile. It very nearly green-lit committing broken T1 work.

## Fix

- run-checks.cmd must propagate a non-zero exit from the build step (stop there — don't fall through to
  test) and from each test invocation. On Windows cmd this means checking `%ERRORLEVEL%` after every
  `dotnet` call (or `|| exit /b 1`), since consecutive commands otherwise swallow failures.
- Also treat "test source file not found" as failure (it implies a build/test drift even if build exits 0).
- Verify by re-introducing a temporary compile error and confirming the script exits non-zero at the build
  step, then a temporary failing test ditto.

Trivial-to-standard rigor: it's a script fix, but it IS the gate — verify the failure paths by hand.
