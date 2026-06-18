# Review Request: Godot Client Core, Visual Client, and Shared Workflow

## 1. Intent & Scope

This branch implements the Godot-client and shared-workflow todo batch on `D:\MMO`: S15 adds a pure
C# `Mmo.Client.Core`; S16 adds and manually verifies the Godot isometric client view with movement,
chat, metrics overlay, and visual-check launcher; N12/N13 move shared skills, project startup
instructions, and durable memory into `.shared/`; S17 remains blocked because its local-prediction
request conflicts with the current no-prediction roadmap/design text and needs an Orchestrator
decision. Source of truth: `todo/README.md`, `.shared/project.md`, `docs/godot-client-design.md`,
`docs/networking-design-plan.md`, `docs/feature-roadmap.md`, and the todo files in this branch.
Branch: `review/tile-step-todo`. Base: `16f480a docs: add review request for S14`. Implementation
head to review: `24dd9ee blocked: S17 prediction needs architecture decision`.

## 2. How To See The Changes

Run:

```powershell
git diff 16f480a..24dd9ee --stat
git log --oneline 16f480a..24dd9ee
git diff 16f480a..24dd9ee
```

Committed implementation/history in this batch:

```text
24dd9ee blocked: S17 prediction needs architecture decision
62adc83 fix: S16 complete Godot editor view
750f88e fix: S16 Godot chat input and metrics roles
db06ee3 fix: S16 Godot overlay and screen-relative movement
20f0c88 feat: add Godot visual check launcher
a63fff6 docs: update review request after Godot smoke
1efd315 fix: S16 Godot headless run wrapper
b689e01 docs: add review request for Godot client workflow
a28b068 fix: N13 share startup and memory via dotshared
d5babbb fix: N12 share skills via dotshared
1c2edb5 blocked: S17 local prediction waits on Godot view
fbeb562 blocked: S16 Godot editor view
4589df8 fix: S15 Godot client core
8278cc8 chore: add shared agent workflow todo queue
eca0e61 chore: add Godot client todo queue
```

Skip generated/local artifacts: `bin/`, `obj/`, `.godot/`, `.run/`, and the untracked
`src/Mmo.Client.Godot/MmoClientRoot.cs.uid`. Treat this review request file as briefing material,
not implementation. Unrelated uncommitted workspace changes existed and were not staged:
`docs/networking-design-plan.md`, old deleted review request files, `.claude/settings.local.json`,
`docs/godot-client-design.md`, `docs/worldstate-zone-design.md`, `review/README.md`, `start-mmo.cmd`.

## 3. Change Manifest

### Protocol

- No protocol version bump. Protocol remains v9.
- `src/Mmo.Client.Core/MmoClient.cs` consumes existing v9 messages and now exposes the login `Role`
  from `LoginResultMessage`, so UI can decide whether admin-only debug commands are allowed.

### Server

- No production server behavior change.
- `.shared/skills/mmo-dev/scripts/start-godot-visual-check.ps1` sets `MMO_ADMIN_NAMES` for the
  launched debug clients, so `/metrics` works during the manual visual check without changing server
  defaults.

### Clients

- `src/Mmo.Client.Core/*` - new pure C# LiteNetLib client core: connect/login, polling, snapshot ack,
  chunk assembly, entity spawn/despawn, replicated entities, tile interpolation, chat/errors, and
  render states.
- `src/Mmo.Client.Core/ScreenRelativeDirectionMapper.cs` - shared, tested screen-relative WASD to
  world-direction mapping used by Godot (`W` = screen up, `S+D` = screen down-right).
- `src/Mmo.Client.Godot/*` - Godot 4 .NET project and source-authored isometric 3D scene consuming
  `Mmo.Client.Core`: grid/walls/entities/labels, camera follow, confirmed-state glide, visible HUD,
  admin metrics panel, chat log, and real chat input (`Enter`/`T` focus, `Enter` send, `Esc` cancel).
- `.shared/skills/mmo-dev/scripts/start-godot-visual-check.cmd/.ps1` - one-command launcher for
  server plus two visible Godot clients (`GodotA`, `GodotB`).
- `.shared/skills/mmo-dev/scripts/godot-run.ps1` - switched from `Start-Process` to
  `System.Diagnostics.Process` to avoid Windows PowerShell duplicate `Path`/`PATH` environment-key
  failures.
- `.shared/skills/mmo-dev/scripts/stop-mmo.ps1` - cleans Godot client PID files/processes and avoids
  killing the visual-check launcher while it is still starting.

### Persistence

- No production schema/persistence change.
- `tests/Mmo.Client.Core.Tests/TestSqliteDatabase.cs` creates temp migrated SQLite databases for
  real server/client integration tests.

### Tests

- `tests/Mmo.Client.Core.Tests/MmoClientIntegrationTests.cs` - real server/client login, server/zone
  info, role propagation, two-client visibility, movement replication, and chat.
- `tests/Mmo.Client.Core.Tests/TileInterpolatorTests.cs` - local confirmed glide and remote
  interpolation behavior.
- `tests/Mmo.Client.Core.Tests/ScreenRelativeDirectionMapperTests.cs` - screen-relative movement map.
- `Mmo.Client.Core.Tests` added to `Mmo.sln`.

### Docs / Workflow

- `.shared/skills/mmo-dev/*` - canonical shared skill/scripts; `.codex/` and `.claude/` skill files
  are thin stubs.
- `.shared/project.md`, `AGENTS.md`, `CLAUDE.md` - canonical project contract plus root entry stubs.
- `.shared/memory/*` - versioned shared memory notes and index.
- `README.md`, `MMO_PROJECT_PLAN.md`, `docs/runbook.md`, `todo/README.md` - updated shared path and
  workflow references.
- Claude user-memory pointer was created outside git:
  `C:\Users\stefano\.claude\projects\D--MMO\memory\shared-memory-pointer.md`.

### Todo State

- Completed/deleted: `S15-godot-m1-client-core.md`, `S16-godot-m1b-editor-view.md`,
  `N12-share-skills-via-dotshared.md`, `N13-share-startup-and-memory-via-dotshared.md`.
- Remaining blocked: `todo/S17-godot-m2-local-prediction.md`.

## 4. Decisions & Deviations

- I initially blocked S16 because `MMO_GODOT` was not visible, then the user set it. After that,
  `godot-run.cmd` exposed a wrapper bug, which I fixed.
- S16 was completed only after the human visually confirmed the relaunched Godot clients worked:
  map/two players visible, movement direction fixed, chat input working, metrics no longer denied.
- The Godot scene is mostly built in C# from a minimal `Main.tscn` to keep it source-reviewable and
  headless-authorable.
- The Godot metrics panel uses server chat commands (`/metrics`) for now. The visual-check launcher
  elevates `GodotA`/`GodotB` by setting `MMO_ADMIN_NAMES`; production defaults are unchanged.
- `Mmo.Client.Core` creates placeholder metadata if snapshot state arrives before spawn metadata,
  then updates when spawn arrives. This is intentional packet-order tolerance but should be reviewed.
- N13 migrated Claude memory as clean repo-local summaries, not byte-identical copies.
- S17 was not implemented. After S16 was verified, the blocker changed from "needs S16" to an
  architecture conflict: `docs/godot-client-design.md` asks for prediction M2, while
  `docs/feature-roadmap.md` and `docs/networking-design-plan.md` still defer/reject prediction unless
  latency is measured as unacceptable. Since movement was reported correct after S16, I did not
  unilaterally add prediction/protocol changes.

## 5. Self-Verification Evidence

### Final run-checks

Command:

```powershell
.\.shared\skills\mmo-dev\scripts\run-checks.cmd
```

Result:

```text
Build succeeded.
    3 Warning(s)
    0 Error(s)

Passed!  - Failed:     0, Passed:    13, Skipped:     0, Total:    13 - Mmo.Shared.Tests.dll
Passed!  - Failed:     0, Passed:    14, Skipped:     0, Total:    14 - Mmo.Client.Core.Tests.dll
Passed!  - Failed:     0, Passed:    55, Skipped:     0, Total:    55 - Mmo.Server.Tests.dll
```

Warnings were `NU1900` vulnerability-feed warnings because package vulnerability data could not be
loaded from `https://api.nuget.org/v3/index.json`; build/tests passed.

### Godot build/run

Commands:

```powershell
.\.shared\skills\mmo-dev\scripts\godot-build.cmd
.\.shared\skills\mmo-dev\scripts\godot-run.cmd 8
```

Results:

```text
MmoClientGodot -> D:\MMO\src\Mmo.Client.Godot\.godot\mono\temp\bin\Debug\MmoClientGodot.dll
Build succeeded.
    0 Warning(s)
    0 Error(s)

Running Godot headless for ~8 s: D:\MMO\src\Mmo.Client.Godot
(stopped after ~8 s)
----- stdout -----
Godot Engine v4.6.3.stable.mono.official.7d41c59c4 - https://godotengine.org
----- stderr -----
```

Manual visual check:

```text
The user visually verified two launched Godot clients after the overlay/chat fixes:
- movement direction is correct
- chat input works
- metrics no longer produce command-denied spam
- the visible Godot client flow "seems to work"
```

### Fresh 120-client / 60s stress run

Command:

```powershell
.\.shared\skills\mmo-dev\scripts\review-stress.cmd
```

Server-side metrics:

```text
metrics state: uptime=55.2s, tick=1104, peers=121, players=121, stress idle.
metrics 5s: tick/s=19.0, tickMs avg/max=4.13/22.94, driftMs avg/max=7.69/15.36,
budgetMs move/aoi/ser/net/persist/other=0.04/2.64/0.17/0.44/0.00/0.00,
snap/s=2028.0, visible avg/max=58.9/94, clientBytes avg/max=119.9/675,
culled/s=2028.0, out=2011.0kbps, in=215.6kbps,
sendFail/s=0.0, bad/s=0.0, netErr/s=0.0, runtimeFault/s=0.0
metrics 60s: tick/s=20.0, tickMs avg/max=4.23/40.22, driftMs avg/max=7.67/19.39,
budgetMs move/aoi/ser/net/persist/other=0.04/2.67/0.18/0.46/0.00/0.00,
snap/s=1994.7, visible avg/max=73.2/119, clientBytes avg/max=135.4/857,
culled/s=1986.1, out=2340.3kbps, in=210.3kbps,
sendFail/s=0.0, bad/s=0.0, netErr/s=0.0, runtimeFault/s=0.0
metrics total: tickMs last/avg/max=4.21/4.23/40.22, driftMs avg/max=7.67/19.39,
budgetMs avg=0.04/2.67/0.18/0.46/0.00/0.00,
budgetMs max=0.54/6.63/0.74/4.39/0.00/0.34,
snap/s(avg)=1994.7, snapshots=110186, visible avg/max=73.2/119,
clientBytes avg/max=135.4/857, outAvg=2340.4kbps, inAvg=210.3kbps,
sendFail=0, badPackets=0, netErr=0, runtimeFaults=0,
login=121/0, loginMs avg/max=27.1/172.6ms
message metrics: received[ClientHello=121, LoginRequest=121, MoveStep=19491, ChatSend=1, SnapshotAck=110059],
sent[ServerHello=121, LoginResult=121, WorldSnapshot=110186, ChatBroadcast=4, EntitySpawn=13442, EntityDespawn=21192, ZoneInfo=121]
```

Stress client summary:

```text
Summary
  elapsed: 60.168s
  spawned: 120
  connected: 120
  disconnected: 120
  logins accepted/rejected: 120/0
  snapshots: 130243 total, max entities in one snapshot: 119
  protocol messages sent/received: 153868/169288
  protocol bytes sent/received: 1721093/18533225
  avg/max latency: 0.0ms/2ms
  server/network errors: 0/0
```

Protocol compatibility: no new protocol bump in this batch. Protocol remains v9.

## 6. Known Gaps / TODOs / Low-Confidence Areas

- `todo/S17-godot-m2-local-prediction.md` remains blocked on an Orchestrator architecture decision.
- The Godot metrics panel is intentionally simple and backed by `/metrics` chat command text, not a
  dedicated metrics protocol/API.
- Manual visual verification was done by the human, not automated through screenshot assertions.
- Godot UI layout is functional, not polished; reviewer should scrutinize resize/overlap behavior.
- `Mmo.Client.Core` does not yet test packet loss/reordering, reconnect behavior, or long-running
  client churn.

## 7. Highest-Risk Areas To Scrutinize

- `src/Mmo.Client.Core/MmoClient.cs`: snapshot chunk assembly, stale sequence handling, full vs
  partial snapshot pruning, placeholder entity metadata, and ack emission.
- `src/Mmo.Client.Core/TileInterpolator.cs`: local confirmed glide and remote interpolation timing.
- `src/Mmo.Client.Core/ScreenRelativeDirectionMapper.cs`: screen-relative mapping must match the
  isometric camera convention.
- `src/Mmo.Client.Godot/MmoClientRoot.cs`: input focus, chat submission, metrics polling, overlay
  layout, per-frame allocations, camera follow, and view/core separation.
- `.shared/skills/mmo-dev/scripts/start-godot-visual-check.ps1`: temporary admin elevation and
  process cleanup behavior.
- `todo/S17-godot-m2-local-prediction.md`: whether blocking prediction is correct or whether the
  Orchestrator wants `docs/godot-client-design.md` to supersede the no-prediction roadmap.

## 8. What I Want The Reviewer To Do

- Run the repo's code-review workflow and produce a verdict separating BLOCKING issues from nits,
  with file:line references.
- Independently run:

```powershell
.\.shared\skills\mmo-dev\scripts\run-checks.cmd
.\.shared\skills\mmo-dev\scripts\godot-build.cmd
.\.shared\skills\mmo-dev\scripts\godot-run.cmd 8
.\.shared\skills\mmo-dev\scripts\review-stress.cmd
```

- If visual verification is available, run:

```powershell
.\.shared\skills\mmo-dev\scripts\start-godot-visual-check.cmd
```

Then confirm two clients render the map and each other, movement is screen-relative, chat input works,
and the metrics panel populates without command-denied spam. Stop with:

```powershell
.\.shared\skills\mmo-dev\scripts\stop-mmo.cmd
```

- Review `Mmo.Client.Core` for protocol correctness and hot-path allocations.
- Confirm S16 is legitimately complete and S17 is legitimately blocked rather than silently skipped.
- Resolve or explicitly route the plan conflict around local prediction.
- Confirm no rejected-for-this-genre items were introduced: server rewind, lag compensation,
  rollback, lockstep, P2P, LOS/PVS AOI, process split, or hand-rolled reliability.
