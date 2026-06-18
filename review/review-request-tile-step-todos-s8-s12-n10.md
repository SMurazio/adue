# Review Request: Tile-Step Todo Queue S8-S12/N9-N10

## 1. INTENT & SCOPE

This branch implements the review todo queue that followed the tile-stepped movement work: S8 decouples `ClientSession` from authoritative position state, S9 fixes bridge direction parsing, S10 advertises server step speed to clients, N9 adds AOI despawn hysteresis, S11 increases default interest radius to cover the debug view, S12 expands the default world and distributes stress spawns, and N10 reduces hot-path encode buffer churn. Source of truth is `AGENTS.md`, `todo/README.md`, the completed `todo/*.md` files named below, and the existing tile-stepped movement design in `docs/networking-design-plan.md`. Review branch: `review/tile-step-todo`. Diff against base commit `b8a923a` (`fix: N8 zoneinfo map`). Production implementation head before this review-request artifact: `d7b67f9` (`fix: N10 pool encode buffers`).

## 2. HOW TO SEE THE CHANGES

Run these from `D:\MMO`:

```powershell
git diff b8a923a...review/tile-step-todo --stat
git log --oneline b8a923a..review/tile-step-todo
git diff b8a923a...review/tile-step-todo
```

No generated or vendored files are part of these commits. The `review/` directory contains this briefing for the Orchestrator queue; do not treat it as production code. There are unrelated local/orchestrator edits in the worktree (`AGENTS.md`, `README.md`, `MMO_PROJECT_PLAN.md`, `docs/networking-design-plan.md`, `.claude/`, `docs/worldstate-zone-design.md`, `start-mmo.cmd`) that are not part of the implementation commits.

Completed and deleted todo files:

- `todo/S8-decouple-clientsession-position.md`
- `todo/S9-fix-bridge-direction-parse.md`
- `todo/S10-faster-step-speed.md`
- `todo/N9-aoi-despawn-hysteresis.md`
- `todo/S11-interest-radius-covers-view.md`
- `todo/S12-bigger-world-distributed-spawns.md`
- `todo/N10-pool-encode-buffers.md`

Blocked todo files: none. `todo/` now contains only `README.md`.

Implementation history:

```text
d7b67f9 fix: N10 pool encode buffers
1181df6 fix: S12 distributed larger world spawns
b1aa832 chore: update S12 todo
a46bad1 fix: S11 interest radius covers view
4e5f91b chore: add aoi tuning todo queue
6dd132f fix: N9 aoi despawn hysteresis
12ede2b fix: S10 server advertised step speed
ed05bfc chore: add faster step speed todo
c5cd1d6 fix: S9 bridge direction parsing
2e0905c chore: add bridge direction todo
160bfd9 fix: S8 decouple clientsession position
2878f5c chore: add follow-up todo queue
```

## 3. CHANGE MANIFEST

### Protocol / Shared

- `src/Mmo.Shared/Protocol/Messages.cs`: adds `StepCooldownMs` to `ServerHelloMessage`.
- `src/Mmo.Shared/Protocol/ProtocolCodec.cs`: bumps protocol to v11, encodes/decodes `ServerHelloMessage.StepCooldownMs`, and adds `Encode(IProtocolMessage, BinaryWriter)` for reusable writer buffers.
- `tests/Mmo.Shared.Tests/ProtocolCodecTests.cs`: covers v11 server hello round-trip and reusable encode-buffer behavior.

### Server

- `src/Mmo.Server/Runtime/ClientSession.cs`: removes duplicated authoritative tile/facing/revision/step timing from the session; session no longer owns movement state.
- `src/Mmo.Server/Runtime/GameServer.cs`: uses `WorldEntity` as authoritative movement state, sends step cooldown in `ServerHello`, applies AOI exit hysteresis, resolves dynamic spawn origins, and streams snapshot chunks through a reusable encode buffer instead of retaining a per-recipient packet list.
- `src/Mmo.Server/Runtime/Zone.cs`: adds spawn distribution support and central spawn-tile generation for larger worlds.
- `src/Mmo.Server/Configuration/ServerOptions.cs`: changes step cooldown default to 140 ms, interest radius default to 40 tiles, world default to 128x128, and adds validated `MMO_SPAWN_DISTRIBUTION`.
- `src/Mmo.Server/Configuration/SpawnDistribution.cs`: adds `Distributed` and `Clustered` spawn modes.

### Clients

- `src/Mmo.Client.Web/WebBridgeSession.cs`: forwards `StepCooldownMs` to the browser and parses directions strictly as `Direction8` enum names.
- `src/Mmo.Client.Web/Properties/AssemblyInfo.cs`: exposes internals to server tests for bridge parser regression coverage.
- `src/Mmo.Client.Web/wwwroot/app.js`: accepts server-advertised step cooldown and uses it for tile tween timing; initial grid dimensions now match 128x128 defaults.
- `src/Mmo.Client.Console/Program.cs`: prints server step cooldown from `ServerHello`.

### Persistence / Config / Docs

- `.env.example`: updates default `MMO_STEP_COOLDOWN_MS`, `MMO_INTEREST_RADIUS`, world dimensions, and spawn distribution.
- `docs/protocol.md`: documents protocol v11 and `ServerHello.StepCooldownMs`.
- `docs/runbook.md`: documents current defaults and spawn distribution behavior.

### Tests

- `tests/Mmo.Server.Tests/WorldEntityMovementTests.cs`: moves movement authority regression tests from `ClientSession` to `WorldEntity`.
- `tests/Mmo.Server.Tests/ClientSessionTests.cs`: removes stale session-owned movement tests.
- `tests/Mmo.Server.Tests/WebBridgeSessionTests.cs`: verifies all eight `Direction8` names are accepted and legacy direction words are rejected.
- `tests/Mmo.Server.Tests/AoiSelectionTests.cs`: covers AOI hysteresis inclusion/exclusion behavior.
- `tests/Mmo.Server.Tests/AoiIntegrationTests.cs`: keeps AOI enter/leave integration on clustered spawn mode and dynamic spawn coordinates.
- `tests/Mmo.Server.Tests/ServerOptionsTests.cs`: covers new defaults and `MMO_SPAWN_DISTRIBUTION`.
- `tests/Mmo.Server.Tests/WebClientAssetTests.cs`: checks browser handling of server step cooldown.
- `tests/Mmo.Server.Tests/ZoneTests.cs`: covers distributed/clustered spawn tile behavior.
- `tests/Mmo.Server.Tests/Mmo.Server.Tests.csproj`: references `Mmo.Client.Web` for bridge parser tests.

## 4. DECISIONS & DEVIATIONS

- S8: I removed all session-owned movement state instead of trying to keep it mirrored. Runtime authority is now `WorldEntity`; persistence uses the entity tile when present and skips saving if no entity fallback exists.
- S9: I intentionally reject legacy strings such as `up`, `downRight`, and `north`. The bridge now accepts only defined `Direction8` names, case-insensitive. The current web client sends enum names.
- S10: Protocol was bumped from v10 to v11 because `ServerHelloMessage` changed. Old clients should fail the version check rather than silently interoperate.
- N9: AOI hysteresis is a fixed private constant of 1 tile. I did not add a config option because the todo asked for the minimal despawn-stability fix.
- S11: Default interest radius is now 40 tiles. This is based on the current zoomed-out debug view requirement, not a final production AOI budget.
- S12: Default world is now 128x128 and spawn distribution defaults to `distributed`. Clustered mode remains available for deterministic/local tests with `MMO_SPAWN_DISTRIBUTION=clustered`.
- S12: Distributed spawn tiles are deterministic row-major points in a central 64x64 hub with 4-tile spacing, filtered to walkable tiles. I did not randomize spawn order.
- S12: Persisted non-default character tiles are preserved if walkable. Persisted legacy default spawn tile `(8,8)` is treated as old bootstrap state and redistributed.
- N10: The server now reuses a `MemoryStream`/`BinaryWriter` encode buffer and sends snapshot chunks immediately. This is not zero-allocation networking: `WorldSnapshotMessage` and entity snapshot records still allocate, and LiteNetLib copy semantics should be checked.
- The final stress run did not prove a bandwidth reduction from S12. It used the current local SQLite DB; existing character positions can reduce the effect of new distributed spawns. A clean DB comparison is recommended.

## 5. SELF-VERIFICATION EVIDENCE

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

Stopped prior server PID 9064.
Started server PID 11372.

metrics state: uptime=55.3s, tick=1105, peers=121, players=121, stress idle.
metrics 5s: tick/s=16.6, tickMs avg/max=4.04/23.53, driftMs avg/max=8.00/16.02, budgetMs move/aoi/ser/net/persist/other=0.04/2.54/0.17/0.43/0.00/0.00, snap/s=1865.2, visible avg/max=56.4/87, clientBytes avg/max=109.6/633, culled/s=1865.2, out=1692.3kbps, in=196.7kbps, recv/s=2203.8, sent/s=2232.0, move/s=334.6, chat/s=0.2, sendFail/s=0.0, bad/s=0.0, netErr/s=0.0, runtimeFault/s=0.0, login/s=0.2, loginMs avg/max=14.4/14.4ms
metrics 60s: tick/s=20.0, tickMs avg/max=4.22/37.96, driftMs avg/max=7.85/30.01, budgetMs move/aoi/ser/net/persist/other=0.04/2.64/0.19/0.46/0.00/0.00, snap/s=1988.9, visible avg/max=70.8/119, clientBytes avg/max=131.7/850, culled/s=1985.0, out=2272.8kbps, in=209.6kbps, recv/s=2341.8, sent/s=2612.4, move/s=350.3, chat/s=0.0, sendFail/s=0.0, bad/s=0.0, netErr/s=0.0, runtimeFault/s=0.0, login/s=2.2, loginMs avg/max=22.8/171.7ms
metrics total: tickMs last/avg/max=4.93/4.22/37.96, driftMs avg/max=7.85/30.01, budgetMs avg=0.04/2.64/0.19/0.46/0.00/0.00, budgetMs max=0.79/6.66/4.27/1.96/0.00/0.73, snap/s(avg)=1988.9, snapshots=109971, visible avg/max=70.8/119, clientBytes avg/max=131.7/850, outAvg=2272.8kbps, inAvg=209.6kbps, sendFail=0, badPackets=0, netErr=0, runtimeFaults=0, login=121/0, loginMs avg/max=22.8/171.7ms
message metrics: received[ClientHello=121, LoginRequest=121, MoveStep=19368, ChatSend=1, SnapshotAck=109871], sent[ServerHello=121, LoginResult=121, WorldSnapshot=109971, ChatBroadcast=4, EntitySpawn=13379, EntityDespawn=20732, ZoneInfo=121]

elapsed: 60.211s
spawned: 120
connected: 120
disconnected: 120
logins accepted/rejected: 120/0
snapshots: 132459 total, max entities in one snapshot: 118
protocol messages sent/received: 156203/171556
protocol bytes sent/received: 1746897/18209305
avg/max latency: 0.0ms/5ms
server/network errors: 0/0
```

Before/after bandwidth evidence:

- Previous actual baseline available from the prior handoff, not re-run from base in this turn: `metrics 60s: tickMs avg/max=3.82/38.35, visible avg/max=68.5/121, clientBytes avg/max=122.2/871, outAvg=2050.4kbps, inAvg=196.6kbps, sendFail=0, badPackets=0, netErr=0, runtimeFaults=0, login=121/0`.
- Current run after this queue: `clientBytes avg/max=131.7/850`, `outAvg=2272.8kbps`, `visible avg/max=70.8/119`.
- Derived per-client outbound estimate from total outbound: before `2050.4 / 121 = 16.9 kbps/client`; after `2272.8 / 121 = 18.8 kbps/client`.
- Derived per-client snapshot payload estimate at 20 Hz: before `122.2 * 8 * 20 / 1000 = 19.6 kbps/client`; after `131.7 * 8 * 20 / 1000 = 21.1 kbps/client`.
- This queue did not show a clear bandwidth drop in the final run. N9 reduced churn compared with older heavy-despawn runs, and N10 targets encode-buffer allocations, not wire payload size. Re-measure with a clean SQLite DB to isolate S12 spawn distribution.

Protocol compatibility:

- `ProtocolCodec.Version` is now 11.
- `ServerHelloMessage` now carries `StepCooldownMs`.
- Old v10 clients are not expected to be wire-compatible and should fail version validation rather than silently joining.

## 6. KNOWN GAPS / TODOs / LOW-CONFIDENCE AREAS

- I did not manually browser-test movement feel after this queue. The web client code consumes server step cooldown, but the reviewer should verify actual perceived tweening.
- The final stress run used the existing local SQLite data. That means S12's distributed spawn behavior was not isolated from already-persisted character positions.
- Spawn distribution is deterministic row-major, so the first 120 new clients may still be denser than a randomized or hashed distribution.
- `debugVisibilityRadius` in `wwwroot/app.js` is still hardcoded to `96`, while server default interest radius is now 40. This may make the debug ring visually misleading.
- N10 reduces reusable byte-buffer churn but does not eliminate all per-tick allocations. Snapshot message/entity objects still allocate, and the observed tick max remained 37.96 ms.
- `AoiIntegrationTests` still covers enter/leave behavior, but the exact no-despawn-at-boundary timing assertion was removed while stabilizing the hysteresis change. The deterministic predicate test now covers the hysteresis edge.

## 7. HIGHEST-RISK AREAS TO SCRUTINIZE

- `src/Mmo.Server/Runtime/GameServer.cs`: confirm `ProtocolEncodeBuffer` and LiteNetLib `Send(ReadOnlySpan<byte>, ...)` semantics copy bytes before the buffer is reused.
- `src/Mmo.Server/Runtime/GameServer.cs`: verify AOI enter/leave, hysteresis, spawn/despawn, and snapshot chunking still preserve the anti-cheat invariant that outside-AOI entities are never serialized.
- `src/Mmo.Server/Runtime/Zone.cs`: verify distributed spawn math, blocked-tile filtering, and legacy `(8,8)` redistribution are correct with existing databases.
- `src/Mmo.Client.Web/wwwroot/app.js` and `src/Mmo.Client.Web/WebBridgeSession.cs`: verify server-advertised cooldown reaches browser tween timing and all eight screen-relative directions still work.
- `src/Mmo.Shared/Protocol/ProtocolCodec.cs`: verify protocol v11 ordering and server hello decode do not corrupt subsequent messages.
- Tests around movement authority: confirm removing session-owned tile state did not regress persistence-on-disconnect or login/session lifecycle.
- Confirm rejected-for-this-genre items were not introduced: client prediction, pathfinding, LOS, lag compensation, rollback, or process splitting.

## 8. WHAT I WANT THE REVIEWER TO DO

Independently re-run build/tests and stress; do not trust the numbers above. Re-read the diff for correctness and regressions against `docs/networking-design-plan.md`, `docs/feature-roadmap.md`, and the completed todo specs. Confirm docs and code agree, especially protocol v11, step cooldown defaults, interest radius, world dimensions, and spawn distribution. Check the hot snapshot path for per-tick allocations/GC pressure and verify LiteNetLib buffer-copy assumptions. Run a clean-DB stress comparison if possible so S12 distributed spawns are actually measured. Also run at least one browser smoke test for movement feel and the eight direction combinations, especially SE/down-right.

Suggested local commands:

```powershell
cd D:\MMO
.\.codex\skills\mmo-dev\scripts\stop-mmo.cmd
.\.codex\skills\mmo-dev\scripts\run-checks.cmd
.\.codex\skills\mmo-dev\scripts\review-stress.cmd
```

For a custom stress run:

```powershell
.\.codex\skills\mmo-dev\scripts\review-stress.ps1 -Clients 120 -DurationSeconds 60
```

For manual runtime smoke testing:

```powershell
.\.codex\skills\mmo-dev\scripts\start-server.cmd
.\.codex\skills\mmo-dev\scripts\start-web.cmd
```

Produce a verdict that separates BLOCKING issues from nits, with file:line references, and write any actionable findings back into `todo/` using the `S*` before `N*` priority convention.
