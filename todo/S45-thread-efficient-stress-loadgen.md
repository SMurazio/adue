# S45 — Thread-efficient stress load generator (so one box can find the server's real cap)

Severity: should-fix (test tooling — unblocks measuring the true server capacity). Diagnosed during the
post-S41 capacity re-measurement: 500 clients crashed the rig, and the cause is the load generator, not
the server.

## Why (root cause, confirmed in code)

`src/Mmo.Tools.Stress/LoadClient.cs` creates **one `NetManager` per client** (`_client = new NetManager(...)`,
`_client.Start()`), and LiteNetLib's `NetManager.Start()` spins up its **own background receive thread**.
So **N clients ≈ N OS threads** in one process. At ~500 that's ~500 thread stacks (~0.5 GB) plus brutal
context-switch oversubscription on a few cores → the box thrashes and the orchestrating PowerShell dies.
This is a sharp, non-linear **thread-count** cliff (which is why 300 is fine and 500 crashes), and it's a
property of the **load generator**, not the server — the server showed huge headroom at 300 (~2% tick
budget) and never reached its real limit. (Same one-NetManager-per-client shape in the in-game
`SyntheticClientLoad`.)

## What

Re-architect the load generator so client count is decoupled from thread count — drive **all** clients
from a **single poll loop** using **ManualMode** `NetManager`s (no per-client background thread):

1. Create each `LoadClient`'s `NetManager` in **ManualMode** (LiteNetLib: start with manual mode so it
   does NOT spawn a logic/receive thread). The run loop calls `ManualUpdate(deltaMs)` + `PollEvents()` on
   each client every iteration, at a steady cadence (e.g. ~30–60 Hz).
2. The driver runs the whole client fleet from **one thread** (or a small fixed worker pool partitioning
   the clients) — thread count is **O(1)**, not O(N).
3. **Keep one socket per client.** Each client still needs its own `NetManager`/socket (own local port) so
   the server sees **distinct endpoints** — LiteNetLib keys peers by remote endpoint, so many connections
   sharing one socket would collide. Sockets are cheap; it's the *thread per NetManager* we're killing,
   not the socket. (So: N sockets, ~1 driver thread.)
4. Keep the existing staggered spawn (`spawnRate`) and make the max client count comfortably configurable
   so we can ramp to high counts.

## Scope
- Primary: `src/Mmo.Tools.Stress/` (`LoadClient.cs`, the run loop in `Program.cs`/the driver, `StressOptions`
  if a knob is needed). This is the harness the capacity ladder uses.
- Optional bonus: apply the same ManualMode pattern to `src/Mmo.Server/Runtime/SyntheticClientLoad.cs`
  (same per-client-thread flaw). Lower priority — it's polled on the tick thread and pollutes the tick
  measurement, so it isn't the measurement tool. Include only if clean; otherwise note as follow-up.

## Verify
- Network load is not unit-testable; verify the ManualMode loop by careful reading (correct
  `ManualUpdate`/`PollEvents` cadence; events still dispatched; connect/login/move still work). The
  **Orchestrator** runs the real check: a stress run at **500 → 1000+** clients confirming the rig no
  longer explodes on threads, and reads where the **server** metrics (tick budget, AOI, bandwidth, GC)
  actually start to bind — the real cap.

## Acceptance
- Load-gen thread count is independent of client count (one driver loop, ManualMode NetManagers).
- A single box drives ≥1000 clients without the thread-explosion crash; the binding constraint becomes
  the **server's** metrics, not the rig's threads.
- `run-checks.cmd` green (it compiles + existing tests pass). Do NOT commit — Orchestrator reviews and
  runs the high-count ladder.

## Notes
- Confirm the exact LiteNetLib ManualMode API (`NetManager.Start(manualMode: true)` / `ManualUpdate`).
- This is the prerequisite for finishing the S40 capacity study to the server's *true* ceiling on one
  machine (today the doc notes the ~400 single-machine measurement ceiling — this lifts it).
