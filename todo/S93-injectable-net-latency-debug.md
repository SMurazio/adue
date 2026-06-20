# S93 — Live "net latency" debug control: inject artificial latency on the local client

Severity: S (movement test tooling — user request). Client-only; no protocol/server change. Live F5 control,
NOT a launch flag (binds the live-toggle guardrail).

## Why

On LAN the input→server→confirm round-trip is ~1 tick (~50 ms), so the movement models (B cosmetic lead vs S92
accept/deny vs A prediction) feel nearly identical. The user needs to feel them under REAL-WORLD latency without
a remote server — e.g. accept/deny's input lag (the avatar waits ~RTT for the confirmed step) and how model B's
lead/reconcile behaves when confirms arrive late. So: a live, in-client control that injects artificial network
latency on this client's own traffic.

## What to build

A client-side artificial-latency injector with a **live F5 numeric field "Net latency (ms, each way)"**
(default 0), applied without restart. The value is a ONE-WAY delay added symmetrically to BOTH directions, so
the felt round-trip ≈ 2× the value. Setting it to 0 disables injection entirely (zero overhead on the default
path).

### Mechanism — pick the one that actually works in the dev client, justify in the review-request

1. **Preferred if available: LiteNetLib built-in simulation.** `NetManager.SimulateLatency = true`,
   `SimulationMinLatency = SimulationMaxLatency = ms`. This injects symmetric latency on all packets with ~no
   custom code. BUT LiteNetLib gates these behind `#if DEBUG` — **verify they are compiled in and actually take
   effect in the dev Godot client build** (check the build config the `start-godot-visual-check.cmd` /
   `godot-build.cmd` path produces). If DEBUG is defined and a live `SimulateLatency` toggle demonstrably delays
   traffic, use it (set the fields live from the F5 control).
2. **Fallback if (1) is compiled out / unreliable: a minimal custom symmetric delay queue in `MmoClient`.**
   - Outbound: in `Send(...)`, when latency > 0, enqueue `(message, deliveryMethod, releaseAt = _currentTime +
     latency)` instead of sending immediately; in `Poll`, flush every queued item whose `releaseAt <= now`
     (preserve FIFO order per delivery method).
   - Inbound: buffer each parsed incoming `IProtocolMessage` with `releaseAt = _currentTime + latency` and drain
     them in `Poll` in arrival order when due, routing to the existing `HandleMessage`, INSTEAD of handling them
     synchronously in the `PollEvents` callback.
   - latency == 0 ⇒ bypass both queues entirely (immediate send/handle), so the default path is unchanged.

Whichever mechanism: the injected latency must flow through the EXISTING paths so the predictor calibration,
reconcile, accept/deny confirms, etc. all see the delayed snapshots/intents naturally (that delay IS the test).

### F5 control
`src/Mmo.Client.Godot/MmoClientRoot.cs`: add an F5 field "Net latency (ms, each way)" next to the existing
visual fields (same `AddTuningField` + Apply pattern as `camera.zoomMin` etc., or a small int field). On
Apply/Enter it calls a new `MmoClient` API (e.g. `SetSimulatedLatencyMs(int oneWayMs)`), live, no restart.
Admin-gated like the rest of F5. Seed the field from the current value on panel open. Show the active value in
the F3/perf HUD line if cheap (optional, nice-to-have).

## Tests

- If the **custom queue** path is used: unit-test the release ordering/timing — items enqueued with a latency are
  not released before `enqueueTime + latency`, are released at/after it, and preserve arrival order; latency == 0
  bypasses (immediate). Cover both outbound and inbound queues. Keep the whole suite green.
- If the **LiteNetLib built-in** path is used: there is no custom logic to unit-test (it's transport-internal) —
  add at minimum a test/assert that `SetSimulatedLatencyMs` flips the `NetManager` simulation fields as expected,
  and note in the review-request that the actual delay is verified live (Orchestrator), not by unit test.
- `.\.shared\skills\mmo-dev\scripts\run-checks.cmd` green before/after.

## Constraints

- Client-only (client-core + Godot). No protocol/server/wire change. Does not touch movement model logic — it
  only delays this client's I/O so the EXISTING models can be felt under latency.
- Live control only (F5 field), no env-var/launch gating, no restart to change the value. **Diagnostics-are-live
  guardrail.**
- **Safe Local Execution** binds you (scripts only; if a live session locks `Mmo.Shared.dll` during build, stop
  it via `stop-mmo.cmd` and note it). You cannot run Godot — the Orchestrator runs the live check (set 100 ms,
  walk in accept/deny → avatar lags ~RTT; in model B → lead grows, reconcile lands late).
- Do NOT commit, push, or delete the task file — leave the tree dirty + write
  `review/review-request-s93-net-latency-debug.md`; the Orchestrator verifies and commits. (Same loop as S92.)

## Acceptance

- A live F5 "Net latency (ms, each way)" field injects symmetric artificial latency on the local client's
  traffic with no restart; 0 = off (default path unchanged). At e.g. 100 ms the movement models visibly behave
  as under ~200 ms RTT (accept/deny lags, B leads then reconciles late). Mechanism chosen + justified; tests as
  above; run-checks green.
