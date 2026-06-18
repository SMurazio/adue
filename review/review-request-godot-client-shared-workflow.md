# Review Request: Godot Client Core + Shared Agent Workflow

## 1. Intent & Scope

This branch implements the current `todo/` queue on `D:\MMO` after the tile-stepped movement work:
S15 introduces a pure C# `Mmo.Client.Core` and test coverage for the future Godot client, S16 drafts
the Godot isometric view then marks the task blocked because this machine has no Godot .NET runtime
configured, S17 is marked blocked behind S16, N12 moves repo-local skills into `.shared/skills/`,
and N13 moves startup instructions plus durable project memory into `.shared/`. Source of truth:
`todo/README.md`, `AGENTS.md` at task start, `todo/S15-godot-m1-client-core.md`,
`todo/S16-godot-m1b-editor-view.md`, `todo/S17-godot-m2-local-prediction.md`,
`todo/N12-share-skills-via-dotshared.md`, `todo/N13-share-startup-and-memory-via-dotshared.md`,
and the local design notes cited by those tasks. Branch: `review/tile-step-todo`. Base for review:
`16f480a docs: add review request for S14`. Implementation head before this review file:
`a28b068 fix: N13 share startup and memory via dotshared`.

## 2. How To See The Changes

Run:

```powershell
git diff 16f480a..a28b068 --stat
git log --oneline 16f480a..a28b068
git diff 16f480a..a28b068
```

Committed history to review:

```text
a28b068 fix: N13 share startup and memory via dotshared
d5babbb fix: N12 share skills via dotshared
1c2edb5 blocked: S17 local prediction waits on Godot view
fbeb562 blocked: S16 Godot editor view
4589df8 fix: S15 Godot client core
8278cc8 chore: add shared agent workflow todo queue
eca0e61 chore: add Godot client todo queue
```

Skip generated/local artifacts: `bin/`, `obj/`, `.godot/` cache directories, and the untracked
`src/Mmo.Client.Godot/MmoClientRoot.cs.uid`. No vendored runtime dependency was added.

There were unrelated uncommitted user/orchestrator files in the workspace while this was done
(`docs/networking-design-plan.md`, old deleted review files, `.claude/settings.local.json`,
`docs/godot-client-design.md`, `docs/worldstate-zone-design.md`, `review/README.md`,
`start-mmo.cmd`). They were not staged into these task commits.

## 3. Change Manifest

### Protocol

- No shared wire contract change in this batch. Protocol remains v9 from tile-stepped movement.
- `src/Mmo.Client.Core/MmoClient.cs` consumes existing v9 messages, including snapshot sequence ack,
  chunked snapshots, entity spawn/despawn, zone info, chat, and errors.

### Server

- No production server behavior was changed.
- `tests/Mmo.Client.Core.Tests/MmoClientIntegrationTests.cs` starts a real `GameServer` with temp
  SQLite to verify client-core behavior against the actual server.

### Clients

- `src/Mmo.Client.Core/*` - new pure C# client library with LiteNetLib polling, login, snapshot
  assembly, stale snapshot filtering, entity replication, chat, move-step sending, and render-state
  projection.
- `src/Mmo.Client.Core/TileInterpolator.cs`, `MovementCadence.cs`, `RenderPosition.cs` - isolated
  tile interpolation model for confirmed-state rendering; no prediction.
- `src/Mmo.Client.Godot/*` - source-authored Godot 4 .NET project, scene, and C# root node that
  consumes `Mmo.Client.Core`, builds an isometric 3D view, renders ground/grid/walls/entities, sends
  held WASD movement, and can send a startup chat message.
- `Mmo.sln` - includes `Mmo.Client.Core` and `Mmo.Client.Core.Tests`.

### Persistence

- No production persistence/schema change.
- `tests/Mmo.Client.Core.Tests/TestSqliteDatabase.cs` creates disposable SQLite storage for
  integration tests.

### Tests

- `tests/Mmo.Client.Core.Tests/TileInterpolatorTests.cs` - cadence quantization, local confirmed
  movement, and remote interpolation behavior.
- `tests/Mmo.Client.Core.Tests/MmoClientIntegrationTests.cs` - real server/client tests for login,
  server/zone info, two-client visibility, movement replication, and chat.
- `Mmo.Client.Core.Tests` was added to `Mmo.sln`.

### Docs / Workflow

- `.shared/skills/mmo-dev/*` - canonical repo-local skill and scripts.
- `.codex/skills/mmo-dev/SKILL.md` and `.claude/skills/mmo-dev/SKILL.md` - thin stubs pointing to
  the shared skill.
- `.shared/project.md` - canonical shared project contract/startup instructions.
- `AGENTS.md` and `CLAUDE.md` - root entry points pointing to `.shared/project.md`.
- `.shared/memory/*` - canonical version-controlled memory store with migrated project notes.
- `README.md`, `MMO_PROJECT_PLAN.md`, `docs/runbook.md`, `todo/README.md` - updated script paths and
  shared artifact references.
- Claude user-memory pointer created outside the repo at
  `C:\Users\stefano\.claude\projects\D--MMO\memory\shared-memory-pointer.md`.

### Todo State

- Completed/deleted in their fix commits: `S15-godot-m1-client-core.md`,
  `N12-share-skills-via-dotshared.md`, `N13-share-startup-and-memory-via-dotshared.md`.
- Blocked and still present: `S16-godot-m1b-editor-view.md`, `S17-godot-m2-local-prediction.md`.

## 4. Decisions & Deviations

- S16 was not deleted because acceptance requires `godot-run.cmd` and human visual verification with
  two visible Godot clients. I drafted the source-level Godot project and C# view code, verified the
  C# build, then appended `## Blocked`.
- S17 was not implemented because it explicitly depends on S16. I did not start the shared movement
  rule extraction or input-sequence echo; landing prediction protocol work before a verified
  confirmed-state Godot client would violate the queue ordering.
- The Godot scene is mostly built in `MmoClientRoot.cs` from a minimal `Main.tscn`. This keeps the
  work source-reviewable and headless-authorable, but it still needs real editor/runtime validation.
- `Mmo.Client.Core` may create placeholder replicated entities if a snapshot state arrives before
  the corresponding spawn metadata. Later spawn messages update metadata. This avoids dropping valid
  state under packet ordering, but please scrutinize lifecycle behavior.
- N13 migrated the Claude memory notes as clean ASCII repo-local summaries, not byte-identical
  copies. The original user-memory files contained mojibake; the shared memory captures the same
  durable decisions in a versioned form.
- The Claude user-memory pointer is intentionally outside git because Claude auto-loads user memory
  from that location. The repo records the canonical memory in `.shared/memory/`.
- No client prediction, pathfinding, LOS, rollback, lag compensation, process split, or protocol
  bump was introduced.

## 5. Self-Verification Evidence

### Final run-checks

Command:

```powershell
.\.shared\skills\mmo-dev\scripts\run-checks.cmd
```

Result:

```text
Build succeeded.
    4 Warning(s)
    0 Error(s)

Passed!  - Failed:     0, Passed:    13, Skipped:     0, Total:    13 - Mmo.Shared.Tests.dll
Passed!  - Failed:     0, Passed:     5, Skipped:     0, Total:     5 - Mmo.Client.Core.Tests.dll
Passed!  - Failed:     0, Passed:    55, Skipped:     0, Total:    55 - Mmo.Server.Tests.dll
```

Warnings were `NU1900` vulnerability-feed warnings because the sandbox could not load
`https://api.nuget.org/v3/index.json`; build/tests still passed.

### Godot build/run

Command:

```powershell
.\.shared\skills\mmo-dev\scripts\godot-build.cmd
```

Result:

```text
Building Godot client: D:\MMO\src\Mmo.Client.Godot\MmoClientGodot.sln
Mmo.Shared -> D:\MMO\src\Mmo.Shared\bin\Debug\net8.0\Mmo.Shared.dll
Mmo.Client.Core -> D:\MMO\src\Mmo.Client.Core\bin\Debug\net8.0\Mmo.Client.Core.dll
MmoClientGodot -> D:\MMO\src\Mmo.Client.Godot\.godot\mono\temp\bin\Debug\MmoClientGodot.dll

Build succeeded.
    0 Warning(s)
    0 Error(s)
```

Command:

```powershell
.\.shared\skills\mmo-dev\scripts\godot-run.cmd 5
```

Result:

```text
Godot executable not found.
Set MMO_GODOT to your Godot .NET exe (persists for your account):
  setx MMO_GODOT "D:\Tools\Godot\Godot_v4.6.3-stable_mono_win64.exe"
Open a new terminal afterwards, then retry. (Or add godot to PATH.)
```

### Fresh 120-client / 60s stress run

Command:

```powershell
.\.shared\skills\mmo-dev\scripts\review-stress.cmd
```

Server-side metrics captured during stress:

```text
Review stress: clients=120 duration=60s metricsDelay=52s
metrics state: uptime=55.2s, tick=1103, peers=121, players=121, stress idle.
metrics 5s: tick/s=18.0, tickMs avg/max=4.72/24.18, driftMs avg/max=7.34/15.70,
budgetMs move/aoi/ser/net/persist/other=0.04/2.99/0.18/0.51/0.00/0.00,
snap/s=1625.0, visible avg/max=61.7/91, clientBytes avg/max=142.1/661,
culled/s=1625.0, out=1907.8kbps, in=179.1kbps,
sendFail/s=0.0, bad/s=0.0, netErr/s=0.0, runtimeFault/s=0.0
metrics 60s: tick/s=20.0, tickMs avg/max=4.33/39.72, driftMs avg/max=7.78/17.05,
budgetMs move/aoi/ser/net/persist/other=0.03/2.72/0.18/0.48/0.00/0.00,
snap/s=1730.6, visible avg/max=74.7/120, clientBytes avg/max=153.4/864,
culled/s=1725.2, out=2302.6kbps, in=187.1kbps,
sendFail/s=0.0, bad/s=0.0, netErr/s=0.0, runtimeFault/s=0.0
metrics total: tickMs last/avg/max=4.51/4.33/39.72, driftMs avg/max=7.78/17.05,
budgetMs avg=0.03/2.72/0.18/0.48/0.00/0.00,
budgetMs max=0.49/6.43/1.22/3.22/0.00/0.44,
snap/s(avg)=1730.6, snapshots=95506, visible avg/max=74.7/120,
clientBytes avg/max=153.4/864, outAvg=2302.7kbps, inAvg=187.1kbps,
sendFail=0, badPackets=0, netErr=0, runtimeFaults=0,
login=121/0, loginMs avg/max=29.7/357.3ms
message metrics: received[ClientHello=121, LoginRequest=121, MoveStep=19395, ChatSend=1, SnapshotAck=95441],
sent[ServerHello=121, LoginResult=121, WorldSnapshot=95506, ChatBroadcast=4, EntitySpawn=13508,
EntityDespawn=20981, ZoneInfo=121]
```

Stress client summary:

```text
Stress target: 127.0.0.1:7777
Clients=120 duration=60s spawnRate=25/s seed=349512859
Movement every 250ms, direction every 1s, chat=off
Pass criteria: authRate>=100%, errors<=0

Summary
  elapsed: 60.104s
  spawned: 120
  connected: 120
  disconnected: 120
  logins accepted/rejected: 120/0
  snapshots: 112953 total, max entities in one snapshot: 120
  protocol messages sent/received: 136625/152286
  protocol bytes sent/received: 1531467/18351493
  avg/max latency: 0.0ms/4ms
  server/network errors: 0/0
```

Bandwidth comparison: the tile-step task targeted a drop from the earlier observed approximately
112 kbps per client. This fresh run reports server `outAvg=2302.7kbps` over 120 stress clients plus
one metrics client, which is about 19.2 kbps per stress client by total server outbound divided by
120. The stress client's received bytes are 18,351,493 over 60.104s across 120 clients, about
20.4 kbps/client.

Protocol compatibility: protocol version remains v9. This batch did not change shared wire
contracts, so there is no new old/new-client compatibility boundary beyond the existing v9
tile-stepped protocol.

## 6. Known Gaps / TODOs / Low-Confidence Areas

- `todo/S16-godot-m1b-editor-view.md` remains blocked: no Godot .NET executable is configured here,
  and manual visual verification is still required.
- `todo/S17-godot-m2-local-prediction.md` remains blocked behind S16. No prediction work was started.
- The Godot client has not been visually tested with two live clients. The C# build is green only.
- Godot input mapping and camera-relative movement need hands-on verification; browser movement bugs
  motivated this work, but this branch does not alter the web client.
- `Mmo.Client.Core` integration tests cover real server login/move/chat, but not packet loss,
  reordering, chunk loss, reconnect behavior, or long-running churn.
- The shared memory migration is a summarized repo-local version of Claude user-memory, not a
  byte-for-byte archival copy.

## 7. Highest-Risk Areas To Scrutinize

- `src/Mmo.Client.Core/MmoClient.cs`: snapshot chunk assembly, stale sequence handling, full vs
  partial snapshot merge/prune semantics, entity spawn/despawn ordering, and ack emission.
- `src/Mmo.Client.Core/TileInterpolator.cs`: local confirmed-tile glide, remote interpolation timing,
  and whether it can produce visible boundary stalls.
- `tests/Mmo.Client.Core.Tests/MmoClientIntegrationTests.cs`: server lifecycle cleanup, temp SQLite
  isolation, and whether the assertions are strong enough.
- `src/Mmo.Client.Godot/MmoClientRoot.cs`: tile-to-world coordinate mapping, camera framing,
  movement input mapping, per-frame allocations, and separation between Godot view code and client
  core networking.
- `.shared/skills/mmo-dev/*` and docs updates: stale references to `.codex\skills\mmo-dev\scripts`
  or inconsistent startup instructions.
- `.shared/project.md`, `AGENTS.md`, `CLAUDE.md`, `.shared/memory/*`: whether the shared contract is
  complete enough and whether the root stubs are thin enough.

## 8. What I Want The Reviewer To Do

- Run the repo's code-review workflow and produce a verdict separating BLOCKING issues from nits,
  with file:line references.
- Independently run:

```powershell
.\.shared\skills\mmo-dev\scripts\run-checks.cmd
.\.shared\skills\mmo-dev\scripts\review-stress.cmd
.\.shared\skills\mmo-dev\scripts\godot-build.cmd
```

- If Godot .NET is installed, set `MMO_GODOT` and run:

```powershell
.\.shared\skills\mmo-dev\scripts\godot-run.cmd 5
```

- Review `Mmo.Client.Core` for protocol correctness and hot-path allocations.
- Verify S16 and S17 are correctly blocked, not silently incomplete.
- Verify N12/N13 match the shared `.shared/` pattern and that no stale script paths remain in
  committed docs.
- Confirm the branch did not introduce rejected-for-this-genre items: prediction, pathfinding, LOS,
  rollback, lag compensation, lockstep, process split, or hand-rolled reliability.
- Confirm docs/code agree about the current state: Godot confirmed-state client core exists; Godot
  visual runtime is drafted but blocked on local Godot runtime/manual verification; local prediction
  remains future work.
