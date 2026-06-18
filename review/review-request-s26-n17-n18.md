# Review Request: S26 / N17 / N18 Todo Queue

## 1. Intent & Scope

This branch implements the current `todo/` queue from the tile-step review loop: `S26-godot-render-stutter-forward-plus`, `N17-server-log-to-file-option`, and `N18-godot-perf-stats-hud`. Source of truth is `todo/README.md`, the three todo files that are deleted by these commits, `.shared/project.md` step 3, and the existing Godot/client runtime guidance in `docs/runbook.md`. Branch: `review/tile-step-todo`. Review this batch against base commit `7e87629` (`todo: N18 on-screen performance stats HUD for the Godot client`), which is the parent of my first implementation commit in this batch. The branch merge-base with `main` at the time of writing is `6caa63036ea3922c26b47292bd043065a9642f20`, but reviewing from `7e87629` isolates this todo batch.

## 2. How To See The Changes

```powershell
git diff 7e87629...review/tile-step-todo --stat
git log --oneline 7e87629..review/tile-step-todo
git diff 7e87629...review/tile-step-todo
```

Expected implementation commits:

```text
183c700 fix: N18-godot-perf-stats-hud
ca2d398 fix: N17-server-log-to-file-option
f737038 fix: S26-godot-render-stutter-forward-plus
```

This review request file is a handoff artifact; skip it when reviewing runtime behavior. There are unrelated pre-existing working-tree changes outside this batch (`docs/networking-design-plan.md`, deleted older `review/*.md`, deleted `todo/S17...`, untracked docs/scripts). I did not stage or modify those.

## 3. Change Manifest

### Godot Renderer / S26

- `src/Mmo.Client.Godot/project.godot` - removes stale `rendering_device/driver.windows="d3d12"` so the already-selected Compatibility renderer is not contradicted by D3D12 config.
- `tests/Mmo.Server.Tests/GodotClientProjectTests.cs` - adds regression coverage that Godot stays on `GL Compatibility`, not Forward+ / D3D12.
- `docs/runbook.md` - documents why Godot uses `gl_compatibility`.
- `todo/S26-godot-render-stutter-forward-plus.md` - deleted in the S26 commit after completion.

### Server / Launch Scripts / N17

- `src/Mmo.Server/Runtime/Log.cs` - adds `ServerLogWriter`, an opt-in file sink wired through the existing async log thread; writes all log lines to `MMO_SERVER_LOG_FILE` and error lines to `MMO_SERVER_ERR_LOG_FILE`.
- `.shared/skills/mmo-dev/scripts/start-server.ps1` - adds `-LogToFile`, `-LogPath`, and `-ErrorLogPath`; creates `.run/server.log` and `.run/server.err.log`; starts a visible server window via `run-server-window.ps1`.
- `.shared/skills/mmo-dev/scripts/run-server-window.ps1` - new visible-window server wrapper that sets log env vars before launching the repo-local dotnet server.
- `.shared/skills/mmo-dev/scripts/start-godot-visual-check.ps1` - forwards optional log-file switches to `start-server`.
- `.shared/skills/mmo-dev/scripts/*.cmd` - replaces `-ExecutionPolicy Bypass` with `-ExecutionPolicy RemoteSigned`; keeps the scripts runnable on this managed Windows machine without using `Bypass`.
- `.shared/skills/mmo-dev/scripts/stop-mmo.ps1` - recognizes `run-server-window.ps1` as an MMO runtime wrapper when cleaning up.
- `tests/Mmo.Server.Tests/LogTests.cs` - tests that server log writer duplicates all lines to the main log and only errors to the error log.
- `tests/Mmo.Server.Tests/LaunchScriptTests.cs` - tests log-file script hooks and asserts command wrappers do not use `-ExecutionPolicy Bypass` or hidden windows.
- `.shared/skills/mmo-dev/SKILL.md`, `docs/runbook.md` - document `start-server.cmd -LogToFile`.
- `todo/N17-server-log-to-file-option.md` - deleted in the N17 commit after completion.

### Godot Perf HUD / N18

- `src/Mmo.Client.Godot/MmoClientRoot.cs` - adds F3 toggle, throttled 10 Hz HUD text, S25 frame/GC counters, Godot `Performance` metrics, and frame sample feeding.
- `src/Mmo.Client.Godot/FrameTimeGraph.cs` - adds a lightweight `Control` that draws a 120-frame rolling frame-time graph.
- `tests/Mmo.Server.Tests/GodotClientProjectTests.cs` - adds text-level regression coverage for the F3 HUD, performance monitors, managed heap, 10 Hz throttle, and graph hooks.
- `.shared/skills/mmo-dev/SKILL.md`, `docs/runbook.md` - document F3 performance HUD.
- `todo/N18-godot-perf-stats-hud.md` - deleted in the N18 commit after completion.

## 4. Decisions & Deviations

- S26: `project.godot` already had `GL Compatibility` in the branch before my first commit, so I formalized the fix by removing the stale D3D12 driver line, adding a regression test, and documenting the renderer decision. I did not add Forward+ warm-up or shared mesh/material work because the accepted path is Compatibility, not Forward+.
- N17: I did not implement shell-level `Tee-Object` redirection. Instead, the server duplicates its own `Log` output to files from the async logging thread when env vars are present. This keeps the server window visible and avoids hidden redirected processes. Deviation/risk: raw stdout/stderr emitted before `Log` initializes, or native runtime crash output, is not captured by this file sink.
- N17: Removing execution-policy handling entirely broke this managed machine (`running scripts is disabled on this system`). I changed wrappers to `-ExecutionPolicy RemoteSigned`, not `Bypass`. This satisfies the "no Bypass" acceptance while keeping local scripts runnable.
- N17: `start-server.ps1` now records the visible PowerShell wrapper PID in `.run/server.pid`; `stop-mmo.cmd` still stops the wrapper and any repo-local dotnet listener/runtime.
- N18: No CPU% was added, per todo decision. The HUD uses Godot process/physics frame time as the CPU proxy.
- N18: The HUD samples the frame graph every frame but only rebuilds label text every 0.1s; `StringBuilder` is reused to keep allocations low.
- No protocol, movement, prediction, pathfinding, LOS, AOI, or server tick architecture changes were made.

## 5. Self-Verification Evidence

Final `run-checks.cmd`:

```text
Build succeeded.
3 Warning(s) NU1900 package vulnerability lookup warnings due restricted NuGet network.
0 Error(s)
Passed! Mmo.Shared.Tests: Failed 0, Passed 14, Skipped 0, Total 14
Passed! Mmo.Client.Core.Tests: Failed 0, Passed 24, Skipped 0, Total 24
Passed! Mmo.Server.Tests: Failed 0, Passed 74, Skipped 0, Total 74
```

Final Godot build:

```text
MmoClientGodot -> D:\MMO\src\Mmo.Client.Godot\.godot\mono\temp\bin\Debug\MmoClientGodot.dll
Build succeeded.
0 Warning(s)
0 Error(s)
```

Godot headless runtime smoke for the N18 HUD:

```text
Running Godot headless for ~8 s: D:\MMO\src\Mmo.Client.Godot
(stopped after ~8 s)
stdout: Godot Engine v4.6.3.stable.mono.official.7d41c59c4 - https://godotengine.org
stderr: empty
```

N17 launcher smoke:

```text
start-server.cmd -LogToFile
Server started as PID 42700 (Debug); logging to D:\MMO\.run\server.log and D:\MMO\.run\server.err.log
.run\server.log length 1356; tail included:
2026-06-18T17:10:22.2222681+00:00 [info] Starting MMO server on UDP port 7777 at 20 ticks/sec.
2026-06-18T17:10:22.2262881+00:00 [info] Database provider: Sqlite; migrations: D:\MMO\db/sqlite.
2026-06-18T17:10:22.3016831+00:00 [info] Enabled Windows timer resolution: 1ms.
2026-06-18T17:10:22.3064587+00:00 [info] Server listening on UDP 7777.
stop-mmo.cmd stopped PID 42700 (powershell) and PID 32856 (dotnet listener).
```

Fresh 120-client / 60s Release stress run:

```text
Review stress: configuration=Release clients=120 duration=60s metricsDelay=52s
metrics state: uptime=54.6s, tick=1092, peers=121, players=121, stress idle.
metrics 5s: tick/s=17.0, tickMs avg/max=0.86/1.63, driftMs avg/max=0.01/0.16, gc=0/0/0, budgetMs move/aoi/ser/net/persist/other=0.01/0.34/0.05/0.19/0.00/0.00, snap/s=640.8, visible avg/max=58.0/90, clientBytes avg/max=296.3/647, culled/s=640.8, out=1579.4kbps, in=90.8kbps, recv/s=998.8, sent/s=1049.0, move/s=357.2, chat/s=0.2, sendFail/s=0.0, bad/s=0.0, netErr/s=0.0, runtimeFault/s=0.0, login/s=0.2, loginMs avg/max=55.4/55.4ms
metrics 60s: tick/s=20.0, tickMs avg/max=0.88/13.10, driftMs avg/max=0.01/2.49, gc=0/0/0, budgetMs move/aoi/ser/net/persist/other=0.02/0.29/0.07/0.21/0.00/0.00, snap/s=1553.1, visible avg/max=75.5/121, clientBytes avg/max=167.7/871, culled/s=1539.4, out=2263.9kbps, in=172.0kbps, recv/s=1913.7, sent/s=2186.3, move/s=358.3, chat/s=0.0, sendFail/s=0.0, bad/s=0.0, netErr/s=0.0, runtimeFault/s=0.0, login/s=2.2, loginMs avg/max=8.9/162.0ms
metrics total: tickMs last/avg/max=0.84/0.88/13.10, driftMs avg/max=0.01/2.49, gc=0/0/0, budgetMs avg=0.02/0.29/0.07/0.21/0.00/0.00, budgetMs max=1.40/2.16/1.45/1.67/1.33/0.00, snap/s(avg)=1553.1, snapshots=84779, visible avg/max=75.5/121, clientBytes avg/max=167.7/871, outAvg=2264.0kbps, inAvg=172.0kbps, sendFail=0, badPackets=0, netErr=0, runtimeFaults=0, login=121/0, loginMs avg/max=8.9/162.0ms
message metrics: received[ClientHello=121, LoginRequest=121, MoveStep=19557, ChatSend=1, SnapshotAck=84660], sent[ServerHello=121, LoginResult=121, WorldSnapshot=84779, ChatBroadcast=4, EntitySpawn=13490, EntityDespawn=20708, ZoneInfo=121]
Stress Summary: elapsed=60.108s, spawned=120, connected=120, disconnected=120, logins accepted/rejected=120/0, snapshots=91065 total, max entities in one snapshot=121, protocol messages sent/received=114824/129947, protocol bytes sent/received=1291743/17524523, avg/max latency=0.0ms/3ms, server/network errors=0/0.
```

Per-client snapshot bandwidth from server metrics: `outAvg=2264.0kbps / 121 players = ~18.7 kbps/player` during the 60s metrics window. This batch should not materially change protocol bandwidth; stress was run to catch regressions.

Protocol version: unchanged from this batch. No client/server wire contract changes were made.

Completed todos deleted in same commits: `S26-godot-render-stutter-forward-plus.md`, `N17-server-log-to-file-option.md`, `N18-godot-perf-stats-hud.md`. Blocked todos: none. `todo/` now contains only `README.md`.

## 6. Known Gaps / TODOs / Low-Confidence Areas

- S26 and N18 still need human visual validation in a real Godot window. I can verify build/headless startup, but I cannot prove "movement feels smooth" or that F3 does not perceptibly add hitches without the human looking at the client.
- `server.err.log` captures only `Log.Error` entries, not arbitrary native stderr or crash output before the server logging system initializes.
- The F3 HUD may need visual layout tuning in the actual Godot window; it is hidden by default and placed under the top-left status label.
- The new file logging uses per-line `File.AppendAllText` from the async log thread. It is opt-in and off the simulation thread, but a reviewer should judge if this is acceptable for larger logs.
- `start-server.ps1` now starts a visible PowerShell wrapper. This matched the safe script path and smoke-tested stop behavior, but launcher/window behavior is platform-sensitive.

## 7. Highest-Risk Areas To Scrutinize

- Godot `Performance.Monitor.*` values in Compatibility renderer: compile passes, but confirm live values are sane and do not throw/return misleading values.
- F3 HUD allocations/frame pacing: verify the 10 Hz text throttle and graph drawing do not create new hitches.
- `FrameTimeGraph` drawing math and sample ring ordering.
- `start-server.cmd -LogToFile` on this managed Windows setup and on a normal dev setup; especially `RemoteSigned` vs execution policy restrictions.
- `stop-mmo.cmd` cleanup now that the server PID is a PowerShell wrapper PID, with repo-local dotnet stopped separately.
- File logging failure isolation: bad paths/locked files must not crash the server.
- S26 renderer config: confirm Godot editor still reports Compatibility and not Forward+ / D3D12.

## 8. What I Want The Reviewer To Do

Independently re-run the checks; do not trust the numbers above. Re-read the diff for correctness and scope control against `todo/S26...`, `todo/N17...`, and `todo/N18...` as represented by the deleted task files. Confirm docs and `.shared/skills/mmo-dev/SKILL.md` match the runtime behavior. Confirm the hot path did not gain per-frame allocations beyond the intended HUD graph sample, and that the rejected-for-this-genre items (prediction, pathfinding, LOS, protocol changes) were not introduced.

Run:

```powershell
.\.shared\skills\mmo-dev\scripts\run-checks.cmd
.\.shared\skills\mmo-dev\scripts\godot-build.cmd
.\.shared\skills\mmo-dev\scripts\godot-run.cmd 8
.\.shared\skills\mmo-dev\scripts\review-stress-release.cmd --clients=120 --duration=60s
.\.shared\skills\mmo-dev\scripts\start-server.cmd -LogToFile
.\.shared\skills\mmo-dev\scripts\stop-mmo.cmd
.\.shared\skills\mmo-dev\scripts\start-godot-visual-check.cmd -LogToFile
```

For the live visual check, verify: Godot still uses Compatibility renderer; movement remains smooth; F3 toggles a HUD showing FPS, frame ms, process/physics ms, draw calls, objects, primitives, video/static/managed memory, node count, GC counts, hitch count, and a rolling frame-time graph; the HUD itself does not visibly introduce stutter; `.run/server.log` receives server lines while the server window stays visible; `stop-mmo.cmd` closes the server and clients.

Produce a verdict separating BLOCKING issues from nits, with file:line references.
