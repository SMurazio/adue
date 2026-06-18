# Review Request: S14 Remote Interpolation Lag

## 1. INTENT & SCOPE

This branch implements `todo/S14-reduce-remote-interpolation-lag.md`: reduce the web client's remote interpolation buffer after S13's `2x` cadence proved too laggy and caused visible overshoot/rubber-band when remote players stop or turn. Source of truth is `AGENTS.md`, `todo/README.md`, the completed S14 todo file, and the no-prediction/tweening stance in `docs/networking-design-plan.md`. Review branch: `review/tile-step-todo`. Diff against base commit `8b6e22f` (`docs: add review request for movement polish todos`). Production implementation head before this review-request artifact: `e9ad744` (`fix: S14 reduce remote interpolation lag`).

## 2. HOW TO SEE THE CHANGES

Run these from `D:\MMO`:

```powershell
git diff 8b6e22f...review/tile-step-todo --stat
git log --oneline 8b6e22f..review/tile-step-todo
git diff 8b6e22f...review/tile-step-todo
```

No generated or vendored files are part of this implementation. There are unrelated local/orchestrator changes in the worktree that are not part of these implementation commits, including `.codex/skills/mmo-dev/SKILL.md`, `AGENTS.md`, `README.md`, `MMO_PROJECT_PLAN.md`, `docs/networking-design-plan.md`, Godot-related files, deleted prior review request files, and `start-mmo.cmd`.

Completed and deleted todo file:

- `todo/S14-reduce-remote-interpolation-lag.md`

Blocked todo files: none. `todo/` now contains only `README.md`.

Implementation history:

```text
e9ad744 fix: S14 reduce remote interpolation lag
30add5d chore: add remote interpolation lag todo
```

## 3. CHANGE MANIFEST

### Clients

- `src/Mmo.Client.Web/wwwroot/app.js`: changes `remoteInterpolationCadenceMultiplier` from `2` to `1.3`, reducing the current remote interpolation delay from about 300 ms to about 195 ms at the default effective 150 ms server step cadence. Self delay remains `0`.

### Tests

- `tests/Mmo.Server.Tests/WebClientAssetTests.cs`: updates the pinned asset assertion from `2` to `1.3` so future edits do not silently restore the over-buffered setting.

### Protocol / Server / Persistence / Docs

- No protocol, server, persistence, or docs changes in this task.

## 4. DECISIONS & DEVIATIONS

- I used exactly `1.3` as the remote interpolation multiplier because the todo requested moving toward `~1.3x`.
- I did not perform additional tuning by feel inside this implementation pass. The acceptance criteria require by-eye verification, so the reviewer should manually smoke-test this in the web client.
- I left `selfMovementInterpolationDelayMs = 0` untouched as required.
- I did not chase local-avatar snap/lag, client prediction, Godot work, movement direction mapping, pathfinding, LOS, or server snapshot behavior.

## 5. SELF-VERIFICATION EVIDENCE

Baseline before edits:

```text
Initial run-checks attempt failed because existing server/web processes locked build DLLs:
locked by .NET Host (22180) and .NET Host (40620).
Ran .\.codex\skills\mmo-dev\scripts\stop-mmo.cmd, then reran checks successfully.

Baseline after stop-mmo:
Build succeeded.
Mmo.Shared.Tests: Passed 13/13.
Mmo.Server.Tests: Passed 55/55.
```

Final checks:

```text
.\.codex\skills\mmo-dev\scripts\run-checks.cmd

Build succeeded.
Warnings:
NU1900: Error occurred while getting package vulnerability data: Unable to load the service index for source https://api.nuget.org/v3/index.json.

Mmo.Shared.Tests: Passed 13/13.
Mmo.Server.Tests: Passed 55/55.
```

Fresh 120-client / 60s stress run:

```text
.\.codex\skills\mmo-dev\scripts\review-stress.cmd

Review stress: clients=120 duration=60s metricsDelay=52s
Stopped prior server PID 37144.
Started server PID 22540.

metrics state: uptime=55.3s, tick=1105, peers=121, players=121, stress idle.
metrics 5s: tick/s=17.0, tickMs avg/max=4.43/20.56, driftMs avg/max=7.77/16.01, budgetMs move/aoi/ser/net/persist/other=0.03/2.80/0.19/0.48/0.00/0.00, snap/s=1579.4, visible avg/max=57.9/92, clientBytes avg/max=131.9/661, culled/s=1579.4, out=1722.8kbps, in=169.9kbps, recv/s=1899.4, sent/s=1948.4, move/s=342.4, chat/s=0.2, sendFail/s=0.0, bad/s=0.0, netErr/s=0.0, runtimeFault/s=0.0, login/s=0.2, loginMs avg/max=15.4/15.4ms
metrics 60s: tick/s=20.0, tickMs avg/max=4.32/46.48, driftMs avg/max=7.67/19.41, budgetMs move/aoi/ser/net/persist/other=0.04/2.69/0.19/0.48/0.00/0.00, snap/s=1958.8, visible avg/max=73.0/121, clientBytes avg/max=136.9/864, culled/s=1942.9, out=2325.1kbps, in=207.1kbps, recv/s=2312.7, sent/s=2601.5, move/s=351.9, chat/s=0.0, sendFail/s=0.0, bad/s=0.0, netErr/s=0.0, runtimeFault/s=0.0, login/s=2.2, loginMs avg/max=33.2/340.2ms
metrics total: tickMs last/avg/max=5.36/4.32/46.48, driftMs avg/max=7.67/19.41, budgetMs avg=0.04/2.69/0.19/0.48/0.00/0.00, budgetMs max=0.57/6.15/0.84/2.03/0.00/0.57, snap/s(avg)=1958.8, snapshots=108307, visible avg/max=73.0/121, clientBytes avg/max=136.9/864, outAvg=2325.1kbps, inAvg=207.1kbps, sendFail=0, badPackets=0, netErr=0, runtimeFaults=0, login=121/0, loginMs avg/max=33.2/340.2ms
message metrics: received[ClientHello=121, LoginRequest=121, MoveStep=19455, ChatSend=1, SnapshotAck=108176], sent[ServerHello=121, LoginResult=121, WorldSnapshot=108307, ChatBroadcast=4, EntitySpawn=13483, EntityDespawn=21687, ZoneInfo=121]

Summary
  elapsed: 60.179s
  spawned: 120
  connected: 120
  disconnected: 120
  logins accepted/rejected: 120/0
  snapshots: 126497 total, max entities in one snapshot: 120
  protocol messages sent/received: 150097/166353
  protocol bytes sent/received: 1679587/18508354
  avg/max latency: 0.0ms/1ms
  server/network errors: 0/0
```

Protocol compatibility:

- No protocol change in S14. Protocol remains v12 from the prior N11 work.

## 6. KNOWN GAPS / TODOs / LOW-CONFIDENCE AREAS

- I did not manually browser-test remote movement feel. The reviewer should verify the acceptance criteria by eye.
- The best multiplier may still require one more small adjustment after human testing. This pass uses the todo's requested `~1.3x` target.
- Stress testing only proves runtime health; it does not validate visible overshoot or per-step pauses.

## 7. HIGHEST-RISK AREAS TO SCRUTINIZE

- `src/Mmo.Client.Web/wwwroot/app.js`: confirm `1.3x` provides enough jitter slack to avoid underrun without reintroducing per-step pauses.
- `src/Mmo.Client.Web/wwwroot/app.js`: confirm reduced remote delay eliminates visible overshoot/rubber-band on stop/turn.
- `src/Mmo.Client.Web/wwwroot/app.js`: confirm local player remains delay `0` and no local prediction or buffering was introduced.
- `tests/Mmo.Server.Tests/WebClientAssetTests.cs`: confirm the test is adequate for this narrow asset-level behavior.

## 8. WHAT I WANT THE REVIEWER TO DO

Independently re-run build/tests and the stress run; do not trust the numbers above. Re-read the diff against `todo/S14-reduce-remote-interpolation-lag.md` and confirm the scope is limited to remote interpolation buffer tuning. Manually smoke-test the web client with two visible players: stop and turn one player while observing the other, and verify remotes glide without laggy overshoot/correction and without a per-step pause. Confirm the local avatar behavior was not changed and that no rejected-for-this-genre items were introduced: prediction, pathfinding, LOS, rollback, lag compensation, or process splitting.

Suggested local commands:

```powershell
cd D:\MMO
.\.codex\skills\mmo-dev\scripts\stop-mmo.cmd
.\.codex\skills\mmo-dev\scripts\run-checks.cmd
.\.codex\skills\mmo-dev\scripts\review-stress.cmd
```

For manual runtime smoke testing:

```powershell
.\.codex\skills\mmo-dev\scripts\start-server.cmd
.\.codex\skills\mmo-dev\scripts\start-web.cmd
```

Produce a verdict that separates BLOCKING issues from nits, with file:line references, and write any actionable findings back into `todo/` using the `S*` before `N*` priority convention.
