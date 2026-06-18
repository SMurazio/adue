# REVIEW REQUEST: S21 Precise Tick Scheduling

## 1. INTENT & SCOPE

This branch implements `todo/S21-precise-tick-scheduling.md`: fix the confirmed Windows oversleeping tick loop that made authoritative tile movement intermittently slow down for every observer. Source of truth is the S21 todo plus S20 movement trace evidence. Scope is server scheduling only: no gameplay, no protocol change, no client prediction/interpolation change, no allocation/GC tuning. Diff branch `review/tile-step-todo` at `20f88bc8682805ed980c294464aaf42bb4deef33` against base commit `6d389039c18cfd4dd4678dcbc38b1b0a18d8b8aa`.

## 2. HOW TO SEE THE CHANGES

```powershell
git diff 6d389039c18cfd4dd4678dcbc38b1b0a18d8b8aa...20f88bc8682805ed980c294464aaf42bb4deef33 --stat
git diff 6d389039c18cfd4dd4678dcbc38b1b0a18d8b8aa...20f88bc8682805ed980c294464aaf42bb4deef33
git log --oneline 6d389039c18cfd4dd4678dcbc38b1b0a18d8b8aa..20f88bc8682805ed980c294464aaf42bb4deef33
```

Committed history for this task:

```text
20f88bc fix: S21 precise tick scheduling
```

Generated `.run/` harness outputs should be skipped if present locally.

## 3. CHANGE MANIFEST

Server:

- `src/Mmo.Server/Runtime/GameServer.cs`: replaces `DateTimeOffset` + fixed `Task.Delay(1)` scheduling with `Stopwatch` deadlines, precise drift calculation, bounded deadline-aware waits, and Windows timer-resolution setup/cleanup.
- `src/Mmo.Server/Runtime/PreciseTickScheduler.cs`: pure helper for tick interval conversion, catch-up counting, deadline delay calculation, and final spin-to-deadline wait.
- `src/Mmo.Server/Runtime/WindowsTimerResolutionScope.cs`: Windows-only `winmm` `timeBeginPeriod(1)` / `timeEndPeriod(1)` scope, no-op on non-Windows.

Tests:

- `tests/Mmo.Server.Tests/PreciseTickSchedulerTests.cs`: covers catch-up counting and the delay/spin-window math.

Docs:

- `docs/runbook.md`: documents that the server uses `Stopwatch` deadlines and requests 1 ms Windows timer resolution while still polling network events between ticks.

Todo status:

- Completed and removed locally: `todo/S21-precise-tick-scheduling.md`.
- Note: S21 was untracked in this worktree, so its deletion is not visible in commit `20f88bc`.
- Blocked files remaining: none. `todo/` contains only `README.md` at handoff time.

## 4. DECISIONS & DEVIATIONS

- Implemented the recommended combined approach: Windows `timeBeginPeriod(1)` plus `Stopwatch` deadline scheduling.
- Kept network polling responsive. The server does not sleep until the whole next tick; it delays at most 2 ms before returning to poll, and only spins during the final 1.5 ms deadline window.
- Kept the existing catch-up loop. It should rarely fire now, but remains as protection after actual long ticks.
- No protocol version bump, no message shape change, no client movement behavior change.
- No GC/allocation optimization was attempted because S20 confirmed this issue was timer oversleep, not GC.

## 5. SELF-VERIFICATION EVIDENCE

Baseline before coding S21:

```text
Build succeeded.
Passed!  - Failed:     0, Passed:    13, Skipped:     0, Total:    13, Duration: 96 ms - Mmo.Shared.Tests.dll (net8.0)
Passed!  - Failed:     0, Passed:    23, Skipped:     0, Total:    23, Duration: 1 s - Mmo.Client.Core.Tests.dll (net8.0)
Passed!  - Failed:     0, Passed:    61, Skipped:     0, Total:    61, Duration: 5 s - Mmo.Server.Tests.dll (net8.0)
```

Final `.\.shared\skills\mmo-dev\scripts\run-checks.cmd`:

```text
Build succeeded.
    3 Warning(s)
    0 Error(s)
Passed!  - Failed:     0, Passed:    13, Skipped:     0, Total:    13, Duration: 89 ms - Mmo.Shared.Tests.dll (net8.0)
Passed!  - Failed:     0, Passed:    23, Skipped:     0, Total:    23, Duration: 1 s - Mmo.Client.Core.Tests.dll (net8.0)
Passed!  - Failed:     0, Passed:    65, Skipped:     0, Total:    65, Duration: 5 s - Mmo.Server.Tests.dll (net8.0)
```

Warnings were existing restricted-network `NU1900` vulnerability lookup warnings.

Movement trace before S21, from the S20 harness evidence / S21 todo:

```text
tick_hitch examples before: interMs=58.376 driftMs=9.107 gc0/1/2=0
tick_hitch examples before: interMs=69.067 driftMs=26.406 gc0/1/2=0
tick_hitch examples before: interMs=61.650 driftMs=13.945 gc0/1/2=0
```

Movement trace after S21:

Command:

```powershell
.\.shared\skills\mmo-dev\scripts\movement-debug-trace.cmd
```

Actual output showed timer resolution was enabled and no `tick_hitch` lines were emitted by the same harness threshold:

```text
2026-06-18T14:06:28.5760388+00:00 [info] Enabled Windows timer resolution: 1ms.
2026-06-18T14:06:28.5789154+00:00 [info] Server listening on UDP 59258.
2026-06-18T14:06:28.9801357+00:00 [info] mmo_trace side=server event=snapshot_carry ts=2026-06-18T14:06:28.9793389+00:00 tick=9 snapshot=1 player="TraceMover" recipient="TraceMover" networkId=3 tile=32,32 facing=S chunk=1/1
2026-06-18T14:06:29.0292373+00:00 [info] mmo_trace side=server event=snapshot_carry ts=2026-06-18T14:06:29.0292262+00:00 tick=10 snapshot=2 player="TraceMover" recipient="TraceMover" networkId=3 tile=33,32 facing=E chunk=1/1
HARNESS result moverTile=TileCoord { X = 36, Y = 32 } watcherSeesMover=True seenTile=TileCoord { X = 36, Y = 32 } lastSeq=4 confirmedSnapshot=5 queueDepth=4 latencyMs=0
```

Stress before/after drift comparison:

- Previous S20 stress reference: `driftMs avg/max=7.85/20.08`.
- S21 stress: `driftMs avg/max=0.02/4.10`.

Fresh `.\.shared\skills\mmo-dev\scripts\review-stress.cmd --clients=120 --duration=60s`:

Server metrics during stress:

```text
metrics state: uptime=55.3s, tick=1105, peers=121, players=121, stress idle.
metrics 5s: tick/s=19.2, tickMs avg/max=4.41/27.02, driftMs avg/max=0.01/0.28, budgetMs move/aoi/ser/net/persist/other=0.05/2.75/0.19/0.48/0.00/0.00, snap/s=2117.8, visible avg/max=56.7/92, clientBytes avg/max=112.2/668, culled/s=2117.8, out=1964.7kbps, in=222.9kbps, recv/s=2497.6, sent/s=2551.0, move/s=389.4, chat/s=0.2, sendFail/s=0.0, bad/s=0.0, netErr/s=0.0, runtimeFault/s=0.0, login/s=0.2, loginMs avg/max=7.4/7.4ms
metrics 60s: tick/s=20.0, tickMs avg/max=4.17/33.61, driftMs avg/max=0.02/4.10, budgetMs move/aoi/ser/net/persist/other=0.05/2.58/0.19/0.46/0.00/0.00, snap/s=2025.6, visible avg/max=71.8/121, clientBytes avg/max=132.0/871, culled/s=2012.4, out=2321.1kbps, in=213.1kbps, recv/s=2380.8, sent/s=2675.9, move/s=353.0, chat/s=0.0, sendFail/s=0.0, bad/s=0.0, netErr/s=0.0, runtimeFault/s=0.0, login/s=2.2, loginMs avg/max=12.3/358.6ms
metrics total: tickMs last/avg/max=3.81/4.17/33.61, driftMs avg/max=0.02/4.10, budgetMs avg=0.05/2.58/0.19/0.46/0.00/0.00, budgetMs max=2.24/5.82/0.87/2.27/1.63/0.57, snap/s(avg)=2025.6, snapshots=111940, visible avg/max=71.8/121, clientBytes avg/max=132.0/871, outAvg=2321.1kbps, inAvg=213.1kbps, sendFail=0, badPackets=0, netErr=0, runtimeFaults=0, login=121/0, loginMs avg/max=12.3/358.6ms
message metrics: received[ClientHello=121, LoginRequest=121, MoveStep=19506, ChatSend=1, SnapshotAck=111819], sent[ServerHello=121, LoginResult=121, WorldSnapshot=111940, ChatBroadcast=4, EntitySpawn=13601, EntityDespawn=21968, ZoneInfo=121]
```

Stress tool summary:

```text
Summary
  elapsed: 60.12s
  spawned: 120
  connected: 120
  disconnected: 120
  logins accepted/rejected: 120/0
  snapshots: 133847 total, max entities in one snapshot: 121
  protocol messages sent/received: 157653/174758
  protocol bytes sent/received: 1762909/18420755
  avg/max latency: 0.0ms/1ms
  server/network errors: 0/0
```

Protocol compatibility:

- `ProtocolCodec.Version` was not changed.
- No wire message fields changed.

Human visual re-check:

- Not performed by me. The task explicitly asks for a human 2-client Godot movement feel check; please do this after review or have the user do it.

## 6. KNOWN GAPS / TODOs / LOW-CONFIDENCE AREAS

- `timeBeginPeriod(1)` has a system-wide timer cost while the server is running. It is cleaned up on shutdown via `timeEndPeriod(1)`.
- The final 1.5 ms spin window intentionally burns a tiny amount of CPU near tick deadlines. Review whether `1.5ms` spin and `2ms` max poll delay are the right defaults.
- The trace harness emits no `tick_hitch` lines after the fix, so the strongest direct runtime number is stress drift (`0.02/4.10ms`) plus snapshot timestamps around 50 ms spacing.
- I did not add an environment switch to disable high-resolution scheduling; if desired, that should be a new todo from the Orchestrator.
- Human Godot feel verification remains outstanding.

## 7. HIGHEST-RISK AREAS TO SCRUTINIZE

- `src/Mmo.Server/Runtime/GameServer.cs`: tick scheduling now uses Stopwatch timestamps; verify catch-up, drift measurement, and cancellation behavior are correct.
- `src/Mmo.Server/Runtime/PreciseTickScheduler.cs`: delay calculation should avoid oversleep while preserving network polling cadence.
- `src/Mmo.Server/Runtime/WindowsTimerResolutionScope.cs`: P/Invoke should be Windows-only, paired begin/end, and no-op safely elsewhere.
- Shutdown path: ensure timer resolution is released after cancellation/exceptions and server resources still flush/stop.
- CPU usage: final spin should not create unacceptable idle CPU cost.
- Existing S20 trace fields and metrics should remain semantically correct after moving from `DateTimeOffset` scheduling to `Stopwatch`.

## 8. WHAT I WANT THE REVIEWER TO DO

Independently re-run:

```powershell
.\.shared\skills\mmo-dev\scripts\run-checks.cmd
.\.shared\skills\mmo-dev\scripts\movement-debug-trace.cmd
.\.shared\skills\mmo-dev\scripts\review-stress.cmd --clients=120 --duration=60s
```

Then run a human two-client Godot visual check:

```powershell
.\.shared\skills\mmo-dev\scripts\start-godot-visual-check.cmd
```

Review the diff against S21 and `.shared/project.md`. Confirm the implementation fixes scheduler oversleep without introducing protocol/gameplay/client prediction/pathfinding/LOS changes. Check hot-loop allocation and CPU implications. Produce a verdict separating BLOCKING issues from nits, with `file:line` references.
