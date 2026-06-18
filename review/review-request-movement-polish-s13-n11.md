# Review Request: Movement Polish S13/N11

## 1. INTENT & SCOPE

This branch implements the current `todo/` queue after the prior tile-step review: `S13-glide-matches-quantized-cadence.md` fixes visible move-stop-move-stop stutter in the web client by matching glide duration to the server's tick-quantized cadence and adding remote interpolation slack; `N11-debug-ring-matches-interest-radius.md` makes the web debug visibility ring reflect the server's actual AOI interest radius. Source of truth is `AGENTS.md`, `todo/README.md`, those two completed todo files, and the existing no-client-prediction/tweening stance in `docs/networking-design-plan.md`. Review branch: `review/tile-step-todo`. Diff against base commit `e1597b2` (`docs: add review request for tile-step todos`). Production implementation head before this review-request artifact: `71d9421` (`fix: N11 debug ring matches interest radius`).

## 2. HOW TO SEE THE CHANGES

Run these from `D:\MMO`:

```powershell
git diff e1597b2...review/tile-step-todo --stat
git log --oneline e1597b2..review/tile-step-todo
git diff e1597b2...review/tile-step-todo
```

No generated or vendored files are part of these commits. The `review/` directory contains this briefing for the Orchestrator queue; do not treat it as production code. There are unrelated local/orchestrator changes in the worktree that are not part of these implementation commits, including `AGENTS.md`, `README.md`, `MMO_PROJECT_PLAN.md`, `docs/networking-design-plan.md`, `.claude/`, `docs/worldstate-zone-design.md`, `start-mmo.cmd`, `review/README.md`, and the deleted prior review request file `review/review-request-tile-step-todos-s8-s12-n10.md`.

Completed and deleted todo files:

- `todo/S13-glide-matches-quantized-cadence.md`
- `todo/N11-debug-ring-matches-interest-radius.md`

Blocked todo files: none. `todo/` now contains only `README.md`.

Implementation history:

```text
71d9421 fix: N11 debug ring matches interest radius
5a2952a fix: S13 glide matches quantized cadence
de43941 chore: add movement polish todo queue
```

## 3. CHANGE MANIFEST

### Protocol / Shared

- `src/Mmo.Shared/Protocol/Messages.cs`: adds `InterestRadiusTiles` to `ServerHelloMessage`.
- `src/Mmo.Shared/Protocol/ProtocolCodec.cs`: bumps protocol v11 -> v12 and encodes/decodes the advertised interest radius.
- `tests/Mmo.Shared.Tests/ProtocolCodecTests.cs`: updates `ServerHello` round-trip coverage for step cooldown plus interest radius.

### Server

- `src/Mmo.Server/Runtime/GameServer.cs`: sends `_options.InterestRadius` in `ServerHello`.

### Clients

- `src/Mmo.Client.Web/WebBridgeSession.cs`: forwards `InterestRadiusTiles` to the browser as `interestRadiusTiles`.
- `src/Mmo.Client.Web/wwwroot/app.js`: computes effective tile glide duration as `ceil(stepCooldownMs / tickIntervalMs) * tickIntervalMs`, sets remote interpolation delay to `2x` that cadence, keeps self interpolation delay at `0`, and updates the debug visibility ring from server-advertised interest radius.
- `src/Mmo.Client.Console/Program.cs`: prints the advertised interest radius in the server hello line.

### Tests

- `tests/Mmo.Server.Tests/WebClientAssetTests.cs`: asserts the web client uses tick-quantized cadence, remote slack, self delay `0`, advertised interest radius, and no stale hardcoded `debugVisibilityRadius = 96`.

### Docs

- `docs/protocol.md`: documents protocol v12 and the new `ServerHello` interest radius field.
- `docs/runbook.md`: notes that the web debug visibility ring uses server-advertised `MMO_INTEREST_RADIUS`.

## 4. DECISIONS & DEVIATIONS

- S13: I used the client-side calculation requested by the todo instead of changing the server to advertise effective cadence. The server still advertises raw `StepCooldownMs`; the browser computes the effective cadence from `stepCooldownMs` and `tickRate`.
- S13: Remote interpolation slack is exactly `2x` the effective cadence. This is inside the requested 1.5-2x range and keeps the code simple.
- S13: Self interpolation delay remains `0` as requested. Any remaining local-player micro-jitter from no prediction plus the web bridge hop is deliberately not addressed here.
- N11: I chose `ServerHello` instead of `ZoneInfo` for the interest radius because the todo preferred advertising it alongside S10's step-cooldown advertisement. This required a protocol bump to v12.
- N11: The browser keeps a fallback radius of `40` for malformed/missing server hello data, matching the current default, but the normal path is server-advertised.
- I did not change movement direction mapping, pathfinding, LOS, prediction, or AOI selection logic beyond the debug ring display.
- I did not perform manual by-eye browser verification of S13 movement smoothness in this session. The regression coverage is static asset coverage plus build/tests/stress.

## 5. SELF-VERIFICATION EVIDENCE

Baseline before edits:

```text
Initial run-checks attempt failed because existing server/web processes locked build DLLs:
locked by .NET Host (32156) and .NET Host (49252).
Ran .\.codex\skills\mmo-dev\scripts\stop-mmo.cmd, then reran checks successfully.
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
Stopped prior server PID 5516.
Started server PID 10996.

metrics state: uptime=55.2s, tick=1104, peers=121, players=121, stress idle.
metrics 5s: tick/s=17.8, tickMs avg/max=3.76/20.77, driftMs avg/max=7.25/15.61, budgetMs move/aoi/ser/net/persist/other=0.04/2.40/0.15/0.40/0.00/0.00, snap/s=1619.2, visible avg/max=51.0/82, clientBytes avg/max=119.6/598, culled/s=1619.2, out=1605.8kbps, in=179.5kbps, recv/s=2006.4, sent/s=2014.6, move/s=363.2, chat/s=0.2, sendFail/s=0.0, bad/s=0.0, netErr/s=0.0, runtimeFault/s=0.0, login/s=0.2, loginMs avg/max=14.2/14.2ms
metrics 60s: tick/s=20.0, tickMs avg/max=3.99/43.47, driftMs avg/max=7.71/16.11, budgetMs move/aoi/ser/net/persist/other=0.04/2.52/0.17/0.43/0.00/0.00, snap/s=1818.2, visible avg/max=71.0/120, clientBytes avg/max=140.8/864, culled/s=1804.8, out=2229.4kbps, in=194.9kbps, recv/s=2174.2, sent/s=2463.0, move/s=353.8, chat/s=0.0, sendFail/s=0.0, bad/s=0.0, netErr/s=0.0, runtimeFault/s=0.0, login/s=2.2, loginMs avg/max=26.9/171.0ms
metrics total: tickMs last/avg/max=4.14/3.99/43.47, driftMs avg/max=7.71/16.11, budgetMs avg=0.04/2.52/0.17/0.43/0.00/0.00, budgetMs max=0.64/9.26/1.33/2.45/0.00/0.44, snap/s(avg)=1818.2, snapshots=100409, visible avg/max=71.0/120, clientBytes avg/max=140.8/864, outAvg=2229.5kbps, inAvg=194.9kbps, sendFail=0, badPackets=0, netErr=0, runtimeFaults=0, login=121/0, loginMs avg/max=26.9/171.0ms
message metrics: received[ClientHello=121, LoginRequest=121, MoveStep=19537, ChatSend=1, SnapshotAck=100288], sent[ServerHello=121, LoginResult=121, WorldSnapshot=100409, ChatBroadcast=4, EntitySpawn=13516, EntityDespawn=21730, ZoneInfo=121]

Summary
  elapsed: 60.115s
  spawned: 120
  connected: 120
  disconnected: 120
  logins accepted/rejected: 120/0
  snapshots: 117605 total, max entities in one snapshot: 120
  protocol messages sent/received: 141290/156984
  protocol bytes sent/received: 1582795/17451936
  avg/max latency: 0.0ms/4ms
  server/network errors: 0/0
```

Protocol compatibility:

- `ProtocolCodec.Version` is now 12.
- `ServerHelloMessage` now carries `InterestRadiusTiles`.
- Old v11 clients are not wire-compatible and should fail version validation rather than silently joining.

## 6. KNOWN GAPS / TODOs / LOW-CONFIDENCE AREAS

- I did not manually verify browser movement smoothness by eye. S13's acceptance asked for visual confirmation; the reviewer should do this in the web client.
- The stress run verifies protocol/runtime health, not visual movement quality.
- The web client calculates effective cadence from raw cooldown and tick rate; if the server later changes cooldown quantization logic, this duplicate formula could drift.
- The debug ring is updated from `ServerHello`, not `ZoneInfo`; if future per-zone interest radii exist, this field would need to move or become zone-scoped.
- The 43.47 ms max tick in the stress run is not caused by this UI/protocol change as far as I can tell, but it remains worth watching.

## 7. HIGHEST-RISK AREAS TO SCRUTINIZE

- `src/Mmo.Client.Web/wwwroot/app.js`: confirm `computeEffectiveStepCadenceMs` exactly matches `ServerOptions.StepCooldownTicks` semantics for normal server tick rates.
- `src/Mmo.Client.Web/wwwroot/app.js`: verify remote interpolation delay of `2x` cadence removes gross move-stop-move-stop without making remotes feel too delayed.
- `src/Mmo.Client.Web/wwwroot/app.js`: confirm self avatar still uses delay `0` and does not accidentally buffer local movement.
- `src/Mmo.Shared/Protocol/ProtocolCodec.cs`: verify protocol v12 `ServerHello` field order and all clients/tools are updated.
- `src/Mmo.Client.Web/WebBridgeSession.cs`: verify JSON camel-casing produces `interestRadiusTiles` and browser receives it.
- Browser movement directions were not changed here; if SE/down-right is still broken, that should remain a separate todo rather than being hidden inside S13/N11.

## 8. WHAT I WANT THE REVIEWER TO DO

Independently re-run build/tests and the stress run; do not trust the numbers above. Re-read the diff for correctness against `todo/S13-glide-matches-quantized-cadence.md`, `todo/N11-debug-ring-matches-interest-radius.md`, and `docs/networking-design-plan.md`'s no-prediction/tweening stance. Manually smoke-test the web client: hold movement and verify both local and remote avatars glide continuously tile-to-tile, with no regular stop between steps; confirm the debug ring radius matches the configured server `MMO_INTEREST_RADIUS`. Confirm docs and code agree about protocol v12 and that old clients are not silently compatible. Also confirm no rejected-for-this-genre items were introduced: prediction, pathfinding, LOS, rollback, lag compensation, or process splitting.

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
