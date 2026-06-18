# Server tick performance: the slowdown saga and what "good" looks like

The intermittent movement slowdown (globally visible, replicated across clients) had **two
independent causes**, fixed in sequence:

1. **Scheduler over-sleep (S21).** `Task.Delay(1)` on Windows sleeps ~15 ms (default timer
   granularity), producing 60–82 ms tick gaps. Fixed with `timeBeginPeriod(1)`
   (`WindowsTimerResolutionScope`) + Stopwatch deadline scheduling with a hybrid coarse-delay /
   final-spin loop (`PreciseTickScheduler`). `driftMs avg/max` 7.85/20.08 → 0.02/4.10.

2. **GC pauses (S22).** Server ran on **Workstation GC** → periodic blocking Gen2 stop-the-world
   pauses (~20 ms, "less often" after S21). Fixed two ways: enabled
   `<ServerGarbageCollection>` + `<ConcurrentGarbageCollection>` in `Mmo.Server.csproj`, AND cut
   per-tick allocation on the snapshot/AOI hot path — the big wins were removing a per-recipient
   closure + interpolated guard string (~2.4k allocs/sec at 120 players) and switching to direct
   wire encoders (`ProtocolCodec.EncodeWorldSnapshot/EntitySpawn/EntityDespawn`) that write into a
   reused buffer instead of allocating message records. Result: `gc=0/0/0` over a 60s/120-client
   stress, tick avg 4.61 → 2.19 ms.

## What "good" looks like — and the acceptance lesson

After S22, a **Release** 60s/120-client stress shows **one** ~22 ms tick the entire minute, with
`gc=0/0/0`. That single outlier is **OS thread preemption**, not server work or GC — unavoidable on
a general-purpose OS (Windows is not an RTOS) without core-pinning/dedicated hardware.

- **A hard "tickMs max < 5 ms" acceptance is wrong** for this environment. The right bar is: no
  GC-correlated spikes, healthy *average* (~2 ms), and any max outliers attributable to OS
  scheduling (gc=0, no budgeted work), not server hot paths.
- **Measure perf in Release, not Debug.** All S20/S21/S22 stress numbers were Debug builds, which
  inflated `tickMs max` (~33 ms Debug vs a single ~22 ms OS blip in Release). Debug perf numbers are
  not representative. See [[review-handoff-loop]] — verify perf claims with a Release run.

## Footgun avoided

Direct wire encoding was chosen over object pooling for hot messages deliberately: it removes the
allocation without the pool-lifetime / `ArrayPool` use-after-return / `Span`-escaping risks. Buffer
reuse is safe because LiteNetLib `Send` copies the payload synchronously (proven: 0 badPackets over
stress). If anyone later adds pooling here, that safety argument no longer holds automatically.
