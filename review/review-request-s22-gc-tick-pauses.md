# REVIEW REQUEST: S22 GC Tick-Pause Mitigation

## 1. Intent & Scope

This branch implements the queued `todo/S22-fix-residual-gc-tick-pauses.md` work item as far as the evidence supports it: add duration-based tick-hitch measurement, surface GC deltas in server metrics, enable server/background GC, and reduce hot-path snapshot allocation. The task is not fully accepted: the S22 todo remains in `todo/` with a `## Blocked` section because post-fix stress still has non-GC tick max outliers above the requested ~5 ms ceiling. Review branch: `review/tile-step-todo`. Diff base for this S22 unit: `dce7a21a6b2fbe641ba79ad146ef0cf5d9fea83c` (`docs: add review request for S21 precise tick scheduling`). S22 commit: `46c113400c7ea7b4fb6a99e3075e6259b01c02cf`.

## 2. How To See The Changes

```powershell
git diff dce7a21a6b2fbe641ba79ad146ef0cf5d9fea83c...review/tile-step-todo --stat
git diff dce7a21a6b2fbe641ba79ad146ef0cf5d9fea83c...review/tile-step-todo
git log --oneline dce7a21a6b2fbe641ba79ad146ef0cf5d9fea83c..review/tile-step-todo
git show --stat 46c113400c7ea7b4fb6a99e3075e6259b01c02cf
```

Generated scratch projects under `.run/` are not committed. There are unrelated uncommitted worktree changes outside this S22 commit; review the committed diff above, not raw `git status`.

## 3. Change Manifest

Protocol:

- `src/Mmo.Shared/Protocol/ProtocolCodec.cs` - added direct server encoders for `WorldSnapshot`, `EntitySpawn`, and `EntityDespawn` so the server can encode hot-path messages without allocating protocol message records.
- `tests/Mmo.Shared.Tests/ProtocolCodecTests.cs` - regression test proving direct server encoders still decode through the normal codec.

Server:

- `src/Mmo.Server/Mmo.Server.csproj` - enabled `ServerGarbageCollection` and `ConcurrentGarbageCollection`.
- `src/Mmo.Server/Configuration/ServerOptions.cs` - added validated `MMO_DEBUG_MOVEMENT_TICK_DURATION_MS` for duration-based hitch tracing.
- `src/Mmo.Server/Runtime/GameServer.cs` - records per-tick GC deltas, removes the per-recipient snapshot closure/string allocation, uses direct snapshot/spawn/despawn encoding, adds an AOI fast path when max-visible is not binding, and reuses one `TickBudgetRecorder`.
- `src/Mmo.Server/Runtime/ServerMetrics.cs` - adds Gen0/1/2 counts to total/window metrics, avoids cloning message arrays for formatted summaries, and records sent bytes by `MessageType`.
- `src/Mmo.Server/Runtime/ServerMovementTrace.cs` - adds duration-triggered tick hitch tracing with GC deltas and `unbudgetedMs`.
- `src/Mmo.Server/Runtime/TickBudgetRecorder.cs` - adds `Reset()` so the recorder can be reused per tick.

Clients:

- No client runtime changes.

Persistence:

- No persistence changes.

Tests:

- `tests/Mmo.Server.Tests/ServerMetricsTests.cs` - asserts GC counts flow into total/window metrics.
- `tests/Mmo.Server.Tests/ServerMovementTraceTests.cs` - asserts gap and duration triggers, GC context, and `unbudgetedMs`.
- `tests/Mmo.Server.Tests/ServerOptionsTests.cs` - asserts the new debug duration env var and default.

Docs / queue:

- `.env.example` - documents `MMO_DEBUG_MOVEMENT_TICK_DURATION_MS=15`.
- `docs/runbook.md` - documents duration threshold, server/background GC, and `gc=gen0/gen1/gen2` metrics.
- `todo/S22-fix-residual-gc-tick-pauses.md` - added as a blocked todo with before/after evidence because the full acceptance criterion is not met.

## 4. Decisions & Deviations

- The task is intentionally not deleted. Acceptance required `tickMs max` well under ~5 ms; the final run still reports `33.57 ms`, so `todo/S22-fix-residual-gc-tick-pauses.md` remains with `## Blocked`.
- I added `unbudgetedMs` to movement trace output. This is a measurement-only extension to make the current non-GC outlier visible in future traces.
- I did not change the protocol version. Wire messages are unchanged; direct encoders write the same v9 payloads.
- I chose direct encoding over object pools for hot snapshot/spawn/despawn messages. This avoids pool lifetime/reset risks and keeps protocol serialization centralized in `Mmo.Shared`.
- I did not introduce `ArrayPool<T>` in the snapshot path. Existing encode buffers are already reused, and LiteNetLib send buffer lifetime is easier to reason about with the current immediate send pattern.
- I added an AOI fast path when `MaxVisibleEntities >= entities.Count`; this still applies the AOI radius/retention predicate and only skips candidate sort/cap work when capping cannot affect the result.
- I did not switch the stress benchmark to Release. The shared `review-stress.cmd`/`start-server.cmd` path runs Debug output today, so the evidence remains comparable to the previous S20/S21 numbers.
- I did not run the manual Godot smoothness re-check; that remains human-only.
- The pre-fix evidence confirms aggregate GC counts (`39/3/2`) during the 60s stress, but I did not capture a per-tick pre-fix `gc2` trace before changing the code. Post-fix runs show `0/0/0` collections while max tick outliers remain.

## 5. Self-Verification Evidence

Before S22 changes, `.\.shared\skills\mmo-dev\scripts\run-checks.cmd` was green:

```text
Build succeeded.
Passed! Mmo.Shared.Tests: 13 passed, 0 failed.
Passed! Mmo.Client.Core.Tests: 23 passed, 0 failed.
Passed! Mmo.Server.Tests: 65 passed, 0 failed.
```

After S22 changes, `.\.shared\skills\mmo-dev\scripts\run-checks.cmd` was green:

```text
Build succeeded.
3 Warning(s), 0 Error(s). Warnings were NU1900 vulnerability-data lookup failures because network access is restricted.
Passed!  - Failed: 0, Passed: 14, Skipped: 0, Total: 14 - Mmo.Shared.Tests.dll
Passed!  - Failed: 0, Passed: 23, Skipped: 0, Total: 23 - Mmo.Client.Core.Tests.dll
Passed!  - Failed: 0, Passed: 66, Skipped: 0, Total: 66 - Mmo.Server.Tests.dll
```

Pre-fix 120-client/60s stress, after adding refined metrics but before the mitigation:

```text
metrics 60s: tick/s=20.0, tickMs avg/max=4.61/35.38, driftMs avg/max=0.02/5.92, gc=39/3/2, budgetMs move/aoi/ser/net/persist/other=0.05/2.76/0.19/0.48/0.00/0.00
Summary: elapsed 60.106s, spawned 120, connected/disconnected 120/120, logins accepted/rejected 120/0, avg/max latency 0.0ms/4ms, server/network errors 0/0.
```

Final post-fix 120-client/60s stress, command:

```powershell
.\.shared\skills\mmo-dev\scripts\review-stress.cmd --clients=120 --duration=60s
```

Actual output excerpt:

```text
metrics state: uptime=55s, tick=1100, peers=121, players=121, stress idle.
metrics 5s: tick/s=19.2, tickMs avg/max=2.20/21.86, driftMs avg/max=0.01/0.07, gc=0/0/0, budgetMs move/aoi/ser/net/persist/other=0.04/0.97/0.14/0.34/0.00/0.00, snap/s=1931.8, visible avg/max=58.1/94, clientBytes avg/max=122.5/682, culled/s=1931.8, out=1959.1kbps, in=206.9kbps, recv/s=2315.0, sent/s=2384.8, move/s=390.2, chat/s=0.2, sendFail/s=0.0, bad/s=0.0, netErr/s=0.0, runtimeFault/s=0.0, login/s=0.2, loginMs avg/max=6.1/6.1ms
metrics 60s: tick/s=20.0, tickMs avg/max=2.19/33.57, driftMs avg/max=0.02/4.26, gc=0/0/0, budgetMs move/aoi/ser/net/persist/other=0.04/0.85/0.15/0.38/0.00/0.00, snap/s=1877.7, visible avg/max=74.7/119, clientBytes avg/max=144.9/850, culled/s=1873.8, out=2355.6kbps, in=200.5kbps, recv/s=2237.1, sent/s=2502.9, move/s=356.9, chat/s=0.0, sendFail/s=0.0, bad/s=0.0, netErr/s=0.0, runtimeFault/s=0.0, login/s=2.2, loginMs avg/max=7.6/163.3ms
metrics total: tickMs last/avg/max=21.86/2.19/33.57, driftMs avg/max=0.02/4.26, gc=0/0/0, budgetMs avg=0.04/0.85/0.15/0.38/0.00/0.00, budgetMs max=1.98/2.88/1.02/2.13/1.88/0.46, snap/s(avg)=1877.7, snapshots=103315, visible avg/max=74.7/119, clientBytes avg/max=144.9/850, outAvg=2355.6kbps, inAvg=200.5kbps, sendFail=0, badPackets=0, netErr=0, runtimeFaults=0, login=121/0, loginMs avg/max=7.6/163.3ms
Summary: elapsed 60.118s, spawned 120, connected/disconnected 120/120, logins accepted/rejected 120/0, snapshots 119976 total, max entities in one snapshot 118, protocol bytes sent/received 1610880/18450056, avg/max latency 0.0ms/4ms, server/network errors 0/0.
```

Movement debug trace harness was run before the final `unbudgetedMs` addition and passed; it emitted a duration/gap hitch during login with `gc0=0 gc1=0 gc2=0`, then completed:

```text
HARNESS result moverTile=TileCoord { X = 36, Y = 32 } watcherSeesMover=True seenTile=TileCoord { X = 36, Y = 32 } lastSeq=4 confirmedSnapshot=5 queueDepth=4 latencyMs=0
```

Protocol compatibility: no protocol version bump; no wire fields changed. Old/new clients at protocol v9 remain compatible with this commit.

## 6. Known Gaps / TODOs / Low-Confidence Areas

- `todo/S22-fix-residual-gc-tick-pauses.md` remains blocked. The GC portion improved, but the explicit `tickMs max <~5 ms` acceptance is not met.
- Remaining max tick outliers are not GC-correlated in the final stress (`gc=0/0/0`) and are mostly outside the current budget buckets. This needs a new plan: deeper profiling/tracing, explicit Debug vs Release benchmark decision, or revised acceptance for OS/runtime outliers.
- Human Godot smoothness check was not run.
- Pre-fix per-tick GC2 correlation was not captured; only aggregate pre-fix collection counts were captured.
- There are unrelated uncommitted worktree changes present in this checkout. They are intentionally not part of S22.

## 7. Highest-Risk Areas To Scrutinize

- `ProtocolCodec` direct encoders: confirm they cannot diverge from normal message encoding and that tests cover enough payload combinations.
- `GameServer` direct send path: confirm `EncodedPacket` buffer lifetime is safe with LiteNetLib sends before the reusable buffer is overwritten.
- AOI fast path: confirm it preserves interest-radius filtering, hysteresis/retention behavior, and max-visible semantics.
- Metrics low-allocation path: confirm `Capture(includeMessageCounts:false)` and `CaptureWindow(includeMessageCounts:false)` do not change `/metrics` output semantics except for avoiding message-array clones.
- `TickBudgetRecorder` reuse: confirm reset happens exactly once per tick and no scope can outlive the tick.
- GC config: confirm it applies to the actual runtime mode used by `start-server.cmd`.
- The blocked conclusion: verify independently whether the remaining max tick is a real runtime problem, a Debug-only artifact, OS scheduling noise, or still a hidden server hot path not covered by current budget buckets.

## 8. What I Want The Reviewer To Do

Please run the repo's code-review workflow against commit `46c1134` and produce a verdict separating BLOCKING issues from nits, with file:line references.

Independently re-run:

```powershell
.\.shared\skills\mmo-dev\scripts\run-checks.cmd
.\.shared\skills\mmo-dev\scripts\review-stress.cmd --clients=120 --duration=60s
.\.shared\skills\mmo-dev\scripts\movement-debug-trace.cmd
```

Then verify:

- The implementation matches `todo/S22-fix-residual-gc-tick-pauses.md` as far as claimed, and the blocked status is justified.
- The committed diff does not include unrelated worktree changes.
- The direct encoders do not change wire compatibility or require a protocol version bump.
- The hot snapshot path has fewer per-tick allocations without unsafe buffer reuse.
- The metrics now make GC and unbudgeted tick time visible enough for the next task.
- The task file remains in `todo/` because acceptance failed, and the review should either define the next todo or tell the implementer exactly what to do to unblock S22.
