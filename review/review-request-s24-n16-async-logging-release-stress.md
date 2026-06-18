# REVIEW REQUEST: S24 Async Logging + N16 Release Perf Measurement

## 1. Intent & Scope

This branch implements two queued review items: `todo/S24-async-logging-off-tick-thread.md` and `todo/N16-release-perf-measurement.md`. S24 addresses the confirmed movement-stutter root cause from `.shared/memory/server-tick-performance.md`: synchronous `Console.WriteLine` on the simulation/network callback path, especially the periodic 10-second tick log. N16 adds a one-command Release stress path so performance acceptance numbers are measured from Release builds. Branch: `review/tile-step-todo`. Diff base: `5a95cabec1387b99fd6f696b27afd1143c0a0964` (`todo: S24 async logging off tick thread`). Implementer commits: `2286395 fix: S24-async-logging-off-tick-thread` and `6411846 chore: N16-release-perf-measurement`.

## 2. How To See The Changes

```powershell
git diff 5a95cabec1387b99fd6f696b27afd1143c0a0964...review/tile-step-todo --stat
git diff 5a95cabec1387b99fd6f696b27afd1143c0a0964...review/tile-step-todo
git log --oneline 5a95cabec1387b99fd6f696b27afd1143c0a0964..review/tile-step-todo
git show --stat 2286395
git show --stat 6411846
```

Do not review raw `git status` as the source of truth; this checkout still has unrelated pre-existing worktree changes/deletions, including old review-file deletions, `docs/networking-design-plan.md`, and `todo/S17-godot-m2-local-prediction.md`.

## 3. Change Manifest

Server:

- `src/Mmo.Server/Runtime/Log.cs` - replaces synchronous console writes with a background log writer; producers enqueue, non-error logs drop on overflow, errors are preserved, and logging exceptions are swallowed.
- `src/Mmo.Server/Runtime/GameServer.cs` - removes the periodic 10-second `tick=... peers=... players=...` log that caused the exact `otherMs ~= 50ms` cadence.
- `src/Mmo.Server/Program.cs` - flushes the async logger on process exit.

Tooling:

- `.shared/skills/mmo-dev/scripts/start-server.ps1` - adds `-Configuration Debug|Release`, defaulting to Debug.
- `.shared/skills/mmo-dev/scripts/stress-test.ps1` - adds `--configuration=Debug|Release` and `--release`, defaulting to Debug.
- `.shared/skills/mmo-dev/scripts/review-stress.ps1` - adds `-Configuration Debug|Release` / `-Release` and builds/runs server, metrics client, and stress tool in that configuration.
- `.shared/skills/mmo-dev/scripts/review-stress-release.cmd` - one-command Release wrapper for review/perf stress runs.

Tests:

- `tests/Mmo.Server.Tests/LogTests.cs` - covers async log flush, non-error overflow drop behavior, and preserving errors when the queue is full.

Docs / queue:

- `.shared/skills/mmo-dev/SKILL.md` - documents `review-stress-release.cmd`.
- `docs/runbook.md` - documents async logging, removal of the periodic tick status log, and the Debug-vs-Release perf measurement rule.
- `todo/S24-async-logging-off-tick-thread.md` - deleted in the S24 commit.
- `todo/N16-release-perf-measurement.md` - deleted in the N16 commit.

## 4. Decisions & Deviations

- I used a custom `LinkedList` + background thread logger instead of `BlockingCollection`. Reason: S24 requires non-error drops on overflow, no synchronous console/file writes from producer threads, and never dropping errors. The custom queue can drop the oldest queued non-error to make room for errors while keeping producer cost to locking/enqueue.
- Error floods can temporarily exceed the nominal queue capacity if the queue is full of errors. This is the tradeoff for "never drop Error" without blocking the tick thread or writing synchronously from producers.
- I removed the periodic 10-second tick status log instead of flag-gating it. `/metrics` is the supported status path and the periodic log was explicitly called operator noise in S24.
- I preserved Debug defaults for existing scripts. Release perf is opt-in via `review-stress-release.cmd`, `review-stress.ps1 -Configuration Release`, or `stress-test.cmd --configuration=Release`.
- I did not run the manual two-Godot-client human smoothness check. I ran the headless movement debug trace and stress scripts; the visual/feel check still needs the human.
- I did not add a test around actual Windows console blocking; the regression is covered by design-level tests that producer calls enqueue/drop without writing.

## 5. Self-Verification Evidence

Baseline before S24:

```text
.\.shared\skills\mmo-dev\scripts\run-checks.cmd
Build succeeded.
Passed! Shared: 14, Client.Core: 23, Server: 66.
```

After S24 and after N16:

```text
.\.shared\skills\mmo-dev\scripts\run-checks.cmd
Build succeeded.
Warnings: NU1900 package vulnerability data lookup failures due restricted network.
Passed!  - Failed: 0, Passed: 14, Skipped: 0, Total: 14 - Mmo.Shared.Tests.dll
Passed!  - Failed: 0, Passed: 23, Skipped: 0, Total: 23 - Mmo.Client.Core.Tests.dll
Passed!  - Failed: 0, Passed: 69, Skipped: 0, Total: 69 - Mmo.Server.Tests.dll
```

S24 Debug 120-client/60s stress, command:

```powershell
.\.shared\skills\mmo-dev\scripts\review-stress.cmd --clients=120 --duration=60s
```

Actual metrics excerpt:

```text
metrics 5s: tick/s=16.2, tickMs avg/max=1.72/2.79, driftMs avg/max=0.01/0.08, gc=0/0/0, budgetMs move/aoi/ser/net/persist/other=0.04/0.84/0.11/0.30/0.00/0.00, sendFail/s=0.0, bad/s=0.0, netErr/s=0.0, runtimeFault/s=0.0
metrics 60s: tick/s=20.0, tickMs avg/max=1.82/12.86, driftMs avg/max=0.01/0.94, gc=0/0/0, budgetMs move/aoi/ser/net/persist/other=0.05/0.76/0.13/0.34/0.00/0.00, out=2390.8kbps, in=208.5kbps, sendFail/s=0.0, bad/s=0.0, netErr/s=0.0, runtimeFault/s=0.0
metrics total: tickMs last/avg/max=1.38/1.82/12.86, driftMs avg/max=0.01/0.94, gc=0/0/0, budgetMs max=1.95/1.70/0.62/1.70/1.47/0.00, sendFail=0, badPackets=0, netErr=0, runtimeFaults=0
Summary: elapsed 60.108s, spawned 120, connected 120, disconnected 120, logins accepted/rejected 120/0, avg/max latency 0.0ms/3ms, server/network errors 0/0.
```

N16 Release 120-client/60s stress, command:

```powershell
.\.shared\skills\mmo-dev\scripts\review-stress-release.cmd --clients=120 --duration=60s
```

Actual metrics excerpt:

```text
Review stress: configuration=Release clients=120 duration=60s metricsDelay=52s
Server started as PID 45740 (Release)
metrics 5s: tick/s=19.8, tickMs avg/max=0.92/1.84, driftMs avg/max=0.00/0.05, gc=0/0/0, budgetMs move/aoi/ser/net/persist/other=0.02/0.34/0.06/0.21/0.00/0.00, snap/s=1730.0, visible avg/max=57.4/91, clientBytes avg/max=138.3/661, out=1974.6kbps, in=193.2kbps, sendFail/s=0.0, bad/s=0.0, netErr/s=0.0, runtimeFault/s=0.0
metrics 60s: tick/s=20.0, tickMs avg/max=0.93/13.23, driftMs avg/max=0.02/7.27, gc=0/0/0, budgetMs move/aoi/ser/net/persist/other=0.02/0.30/0.08/0.23/0.00/0.00, snap/s=1849.3, visible avg/max=73.2/121, clientBytes avg/max=144.5/871, out=2319.3kbps, in=198.2kbps, sendFail/s=0.0, bad/s=0.0, netErr/s=0.0, runtimeFault/s=0.0
metrics total: tickMs last/avg/max=0.69/0.93/13.23, driftMs avg/max=0.02/7.27, gc=0/0/0, budgetMs max=1.97/1.52/0.53/1.68/1.87/0.00, sendFail=0, badPackets=0, netErr=0, runtimeFaults=0
Summary: elapsed 60.168s, spawned 120, connected 120, disconnected 120, logins accepted/rejected 120/0, avg/max latency 0.0ms/2ms, server/network errors 0/0.
```

Movement trace harness after S24/N16:

```text
.\.shared\skills\mmo-dev\scripts\movement-debug-trace.cmd
HARNESS result moverTile=TileCoord { X = 36, Y = 32 } watcherSeesMover=True seenTile=TileCoord { X = 36, Y = 32 } lastSeq=4 confirmedSnapshot=5 queueDepth=4 latencyMs=0
```

It emitted only a small startup gap hitch (`durationMs=7.638`, `otherMs=0`, `gc=0/0/0`) and no 10-second `otherMs ~= 50ms` cadence.

## 6. Known Gaps / TODOs / Low-Confidence Areas

- Manual two-Godot-client human smoothness check is still needed.
- I did not reproduce the pre-fix live Godot 50ms `otherMs` trace in this session; the pre-fix evidence is from the S24 todo/memory note.
- The async logger preserves errors by allowing an all-error flood to exceed nominal capacity. That is intentional but should be reviewed.
- The logger has no explicit process-exit dispose hook; `Program.cs` flushes on normal server exit. Abrupt process termination can still lose queued logs, which is acceptable for console logs.
- The Release stress script uses visible server windows through `start-server.cmd`; this matches current local workflow but is not a headless CI runner.

## 7. Highest-Risk Areas To Scrutinize

- `AsyncConsoleLogSink` concurrency: `_pendingOrWritingCount`, flush signaling, dispose behavior, and preserving order.
- Overflow semantics: non-error logs should drop without blocking; errors should not drop; no producer path should call `Console.WriteLine`.
- Removal of the periodic tick log: confirm no other 10-second synchronous status output remains on the tick path.
- Release script argument forwarding through `.cmd` -> `.ps1`, especially `-Configuration Release` and stress args.
- `review-stress.ps1` metrics client build/run configuration: verify it really uses `bin\Release` for Release runs.
- Stop/cleanup behavior after Release stress: confirm no orphan server/stress dotnet process remains.

## 8. What I Want The Reviewer To Do

Run the repo's code-review skill against `2286395` and `6411846`. Independently rerun:

```powershell
.\.shared\skills\mmo-dev\scripts\run-checks.cmd
.\.shared\skills\mmo-dev\scripts\review-stress-release.cmd --clients=120 --duration=60s
.\.shared\skills\mmo-dev\scripts\movement-debug-trace.cmd
```

Also run the live Godot reproduction if possible:

```powershell
$env:MMO_DEBUG_MOVEMENT='1'
.\.shared\skills\mmo-dev\scripts\start-godot-visual-check.cmd
```

Verify there are no recurring 10-second `tick_hitch` lines with `otherMs ~= 50ms`, and that two-client movement feels smooth. Confirm the docs correctly state Release-only perf acceptance, and that Debug paths still work for fast functional checks. Produce a verdict with BLOCKING issues separated from nits and include file:line references.
