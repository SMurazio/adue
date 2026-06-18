# REVIEW REQUEST: S25 Client-Side Stutter Instrumentation And Mitigation

## 1. Intent & Scope

This branch implements `todo/S25-client-side-stutter-instrument-and-fix.md`, which localized the remaining perceived movement stutter to the Godot client after S21/S22/S24 ruled out server scheduler, GC, and synchronous logging. The source of truth is the S25 todo plus `.shared/memory/server-tick-performance.md`: add client-side frame-hitch instrumentation first, then make low-risk per-frame allocation/render-loop fixes in the Godot client. Branch: `review/tile-step-todo`. Diff base for this unit: `6242e66f6e6b270f9a522af427e5c260d358d04d` (`todo: S25 instrument and fix client-side movement stutter`). Implementation commit: `1a139de546dcd4fca25894c6f18b847ab698baf6`.

## 2. How To See The Changes

```powershell
git diff 6242e66f6e6b270f9a522af427e5c260d358d04d...review/tile-step-todo --stat
git diff 6242e66f6e6b270f9a522af427e5c260d358d04d...review/tile-step-todo
git log --oneline 6242e66f6e6b270f9a522af427e5c260d358d04d..review/tile-step-todo
git show --stat 1a139de546dcd4fca25894c6f18b847ab698baf6
```

Do not review raw `git status` as the diff source. This checkout still has unrelated uncommitted changes/deletions, including `.shared/project.md`, `.shared/memory/MEMORY.md`, `docs/networking-design-plan.md`, old review-file deletions, and `todo/S17-godot-m2-local-prediction.md`.

## 3. Change Manifest

Client core:

- `src/Mmo.Client.Core/ClientMovementTrace.cs` - adds `mmo_trace side=client event=frame_hitch` with frame duration, client GC deltas, interpolation queue depth, cadence, latency, visible count, state, and render position.
- `src/Mmo.Client.Core/MmoClient.cs` - exposes `RecordFrameHitch(...)` so Godot can report frame hitches through the same debug trace sink.

Godot client:

- `src/Mmo.Client.Godot/MmoClientRoot.cs` - samples frame `delta` and per-frame GC deltas, logs client frame hitches behind `MMO_DEBUG_MOVEMENT`, adds frame/GC data to the debug overlay, throttles overlay refresh to 10Hz, only assigns `Label`/`Label3D.Text` when changed, avoids the chat overlay LINQ chain, and replaces the metrics last-line LINQ search with a reverse loop.
- `src/Mmo.Client.Godot/MmoClientGodot.csproj` - enables concurrent GC for the Godot client.

Tests:

- `tests/Mmo.Client.Core.Tests/MmoClientProtocolTests.cs` - verifies frame-hitch traces include GC/context fields when debug is enabled and stay silent when disabled.

Docs / config:

- `.env.example` - adds `MMO_GODOT_FRAME_HITCH_MS=33.3`.
- `docs/runbook.md` - documents the Godot frame-hitch threshold, trace fields, and overlay diagnostics.
- `todo/S25-client-side-stutter-instrument-and-fix.md` - deleted in the implementation commit.

## 4. Decisions & Deviations

- I did not add client prediction or change movement semantics. The change stays within confirmed-state tweening.
- I used Godot `_Process(delta)` as the frame-duration source. This measures delivered frame cadence, which is what the human perceives.
- I log frame hitches from the Godot root only when `delta * 1000 >= FrameHitchThresholdMs`; the default threshold is `33.3ms` and can be changed via `MMO_GODOT_FRAME_HITCH_MS`.
- I could not classify a live reproduced stutter because no manual Godot movement session was run during this implementation. The instrumentation is now present for the human/reviewer to classify client GC vs interpolation starvation vs engine/frame pacing.
- Overlay updates are throttled to 10Hz. That is a deliberate mitigation for per-frame string allocation and label relayout; it may make UI counters update less often, but the gameplay render still updates every frame.
- I enabled concurrent GC, not server GC, for the Godot client. The client is latency-sensitive; the goal is pause reduction rather than server-style throughput.
- The frame-hitch trace uses the existing movement trace sink in `Mmo.Client.Core`, which defaults to `Console.WriteLine`; Godot already surfaces this output in the client console.

## 5. Self-Verification Evidence

Baseline before S25:

```text
.\.shared\skills\mmo-dev\scripts\run-checks.cmd
Build succeeded.
Passed!  - Failed: 0, Passed: 14, Skipped: 0, Total: 14 - Mmo.Shared.Tests.dll
Passed!  - Failed: 0, Passed: 23, Skipped: 0, Total: 23 - Mmo.Client.Core.Tests.dll
Passed!  - Failed: 0, Passed: 69, Skipped: 0, Total: 69 - Mmo.Server.Tests.dll
```

After S25:

```text
.\.shared\skills\mmo-dev\scripts\run-checks.cmd
Build succeeded.
Warnings: NU1900 package vulnerability data lookup failures due restricted network.
Passed!  - Failed: 0, Passed: 14, Skipped: 0, Total: 14 - Mmo.Shared.Tests.dll
Passed!  - Failed: 0, Passed: 24, Skipped: 0, Total: 24 - Mmo.Client.Core.Tests.dll
Passed!  - Failed: 0, Passed: 69, Skipped: 0, Total: 69 - Mmo.Server.Tests.dll
```

Godot C# build:

```text
.\.shared\skills\mmo-dev\scripts\godot-build.cmd
MmoClientGodot -> D:\MMO\src\Mmo.Client.Godot\.godot\mono\temp\bin\Debug\MmoClientGodot.dll
Build succeeded. 0 Warning(s), 0 Error(s).
```

Godot headless smoke:

```text
.\.shared\skills\mmo-dev\scripts\godot-run.cmd 8
Running Godot headless for ~8 s: D:\MMO\src\Mmo.Client.Godot
(stopped after ~8 s)
stdout: Godot Engine v4.6.3.stable.mono.official.7d41c59c4 - https://godotengine.org
stderr: empty
```

Movement debug trace:

```text
.\.shared\skills\mmo-dev\scripts\movement-debug-trace.cmd
HARNESS result moverTile=TileCoord { X = 36, Y = 32 } watcherSeesMover=True seenTile=TileCoord { X = 36, Y = 32 } lastSeq=4 confirmedSnapshot=5 queueDepth=4 latencyMs=0
```

Release 120-client/60s stress:

```text
.\.shared\skills\mmo-dev\scripts\review-stress-release.cmd --clients=120 --duration=60s
Review stress: configuration=Release clients=120 duration=60s metricsDelay=52s
metrics 5s: tick/s=19.0, tickMs avg/max=0.87/1.39, driftMs avg/max=0.00/0.09, gc=0/0/0, budgetMs move/aoi/ser/net/persist/other=0.02/0.33/0.06/0.20/0.00/0.00, snap/s=1895.0, visible avg/max=48.5/79, clientBytes avg/max=108.0/577, culled/s=1895.0, out=1691.1kbps, in=206.3kbps, sendFail/s=0.0, bad/s=0.0, netErr/s=0.0, runtimeFault/s=0.0
metrics 60s: tick/s=20.0, tickMs avg/max=0.88/11.53, driftMs avg/max=0.01/2.06, gc=0/0/0, budgetMs move/aoi/ser/net/persist/other=0.02/0.29/0.07/0.22/0.00/0.00, snap/s=1947.1, visible avg/max=67.3/121, clientBytes avg/max=128.4/864, culled/s=1934.2, out=2180.0kbps, in=206.5kbps, sendFail/s=0.0, bad/s=0.0, netErr/s=0.0, runtimeFault/s=0.0
metrics total: tickMs last/avg/max=0.89/0.88/11.53, driftMs avg/max=0.01/2.06, gc=0/0/0, budgetMs max=1.76/1.25/0.83/1.94/1.29/0.00, sendFail=0, badPackets=0, netErr=0, runtimeFaults=0
Summary: elapsed 60.251s, spawned 120, connected 120, disconnected 120, logins accepted/rejected 120/0, avg/max latency 0.0ms/1ms, server/network errors 0/0.
```

## 6. Known Gaps / TODOs / Low-Confidence Areas

- The required human smoothness re-check has not been run. This is the largest remaining gap.
- I did not capture a live Godot `frame_hitch` sample because the stutter needs manual movement in a visible Godot client. The trace is implemented and should now make that sample visible.
- The overlay is throttled to 10Hz; reviewer should confirm this is responsive enough for chat/status/metrics.
- `FormatMetrics` still allocates a small `List<string>` on each throttled overlay refresh. I left it because the task targets per-frame churn first; further zero-allocation UI formatting can be a follow-up if frame traces still point at GC.
- Enabling concurrent GC may be redundant on some .NET runtimes if background workstation GC was already enabled by default, but the explicit property documents the latency intent.

## 7. Highest-Risk Areas To Scrutinize

- `MmoClientRoot.SampleFrameTiming`: confirm GC deltas and frame duration are interpreted correctly and do not log before `_client` is ready.
- `MmoClientRoot.UpdateOverlay`: confirm 10Hz throttling does not hide important chat/status behavior and still updates debug data clearly.
- `SetTextIfChanged` use for `Label3D`: confirm names still update if metadata changes.
- `ClientMovementTrace.FrameHitch`: confirm trace lines have enough context to distinguish GC, interpolation starvation (`queueDepth=0`/cadence jitter), and engine/frame pacing.
- Godot client csproj GC property: confirm it is honored by Godot .NET builds and does not conflict with export/platform settings.

## 8. What I Want The Reviewer To Do

Run the repo's code-review workflow against `1a139de`. Independently rerun:

```powershell
.\.shared\skills\mmo-dev\scripts\run-checks.cmd
.\.shared\skills\mmo-dev\scripts\godot-build.cmd
.\.shared\skills\mmo-dev\scripts\godot-run.cmd 8
.\.shared\skills\mmo-dev\scripts\review-stress-release.cmd --clients=120 --duration=60s
```

Then run the manual visible check:

```powershell
$env:MMO_DEBUG_MOVEMENT='1'
.\.shared\skills\mmo-dev\scripts\start-godot-visual-check.cmd
```

Move in a single client and with two clients. Verify the top-left overlay shows `MOVE` and `FRAME` lines, and watch client console output for `mmo_trace side=client event=frame_hitch`. Classify any reproduced stutter from the trace fields:

- `gc1/gc2 > 0`: client GC pause.
- `queueDepth=0` or cadence irregularity: interpolation starvation/jitter.
- Neither: Godot engine/frame pacing/render issue.

Produce a verdict with BLOCKING issues separated from nits and include file:line references. If the human still perceives stutter, create the next todo from the actual `frame_hitch` evidence rather than guessing.
