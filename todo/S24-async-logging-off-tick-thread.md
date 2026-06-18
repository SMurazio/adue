# S24 — Move logging off the simulation thread (the real movement-stutter cause)

Severity: **should-fix, user-prioritized.** This is the confirmed root cause of the perceived
movement stutter that survived S21 (scheduler) and S22 (GC).

## Evidence (definitive)

Release server, 2 live Godot clients, `MMO_DEBUG_MOVEMENT=1`, duration-triggered tick-hitch trace:

```
tick=600  trigger=duration durationMs=49.849 driftMs=0.003 gc0=0 gc1=0 gc2=0 ... otherMs=49.843
tick=800  ... durationMs=49.337 ... gc=0/0/0 ... otherMs=49.329
tick=1000 ... durationMs=49.325 ... gc=0/0/0 ... otherMs=49.32
tick=1800/2000/2200/2400/2800/3000 ... ~49-50ms ... gc=0/0/0 ... otherMs~49.x
```

- Hitches recur **every 200 ticks = every 10 s**, each ~**50 ms** (a full tick interval).
- Entirely in **`otherMs`**; `gc=0/0/0`, `driftMs≈0` — not GC, not scheduler, not Debug build.
- `GameServer.TickCore` (line ~422) runs `if (_serverTick % (TickRate*10) == 0)` → `Log.Info(...)`
  inside the `Other` budget scope — exactly the 200-tick cadence and the `otherMs` bucket.
- `Log.cs`: `Log.Info/Warn/Error` → `Console.WriteLine(...)`. Synchronous console I/O on the tick
  thread. A single `Console.WriteLine` to a Windows console blocks ~50 ms (worse with QuickEdit
  selection, which pauses output entirely). A frozen tick delays ALL clients' snapshots at once ⇒
  simultaneous, ~10 s-cadence stutter on every client.

## Why it was missed

- S20's hitch threshold was 75 ms (1.5× interval); a 50 ms freeze was invisible until S22 added the
  duration trigger.
- The same `Console.WriteLine` path also fires on connect/disconnect/auth (`PollEvents` runs on the
  tick thread), which is the login-time hitch noted during S22.

## Fix

**Primary — make logging non-blocking (fixes every log site at once):** route `Log` through a
single background consumer. Producers (tick thread, network callbacks) enqueue to a bounded
`BlockingCollection<string>` / channel; one background thread does the actual `Console.WriteLine`.
The tick thread's cost becomes an enqueue (microseconds).

**Complementary:**
- Drop or flag-gate the periodic per-tick status log (`TickCore` line ~422-428) — it's operator
  noise, not needed every 10 s.
- Audit other hot-path log sites: the per-snapshot `Log.Info` at `GameServer.cs:486` (confirm it is
  not firing per recipient per tick) and the connect/disconnect/auth logs.

**Guardrails:**
- Flush the queue on shutdown (don't lose logs, don't hang on exit). Bounded queue so a logging
  flood can't grow unbounded; on overflow, prefer dropping info over blocking the tick thread, but
  never drop `Error`.
- Keep log ordering stable enough to be readable.
- Don't reintroduce any synchronous `Console`/file write on the tick or network-callback path.

## Acceptance

- Repro (Release server + 2 Godot clients, `MMO_DEBUG_MOVEMENT=1`, ≥60 s): **no ~50 ms `otherMs`
  hitches at the 200-tick cadence**; `tickMs max` stays low (single-digit ms aside from rare OS
  blips). Report before/after `tick_hitch` lines.
- 120-client/60s stress still green; `gc` unaffected.
- **Human re-check:** 2-client Godot movement is smooth, no periodic stutter.
- `run-checks.cmd` green.

See `.shared/memory/server-tick-performance.md`.
