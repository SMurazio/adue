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

## The actual residual stutter: synchronous logging on the tick thread (S24)

The "single ~22 ms OS blip/min in Release" conclusion above was incomplete — it came from the
synthetic stress harness, which did not exercise the periodic-log path the way a live client run
does. A live Release server + 2 Godot clients with the S22 duration trace exposed the real,
user-perceived stutter: **every 200 ticks (10 s) one tick took ~50 ms, entirely in `otherMs`, with
`gc=0` and `driftMs≈0`.** Cause: `GameServer.TickCore`'s periodic status log
(`_serverTick % (TickRate*10) == 0 → Log.Info(...)`), and `Log.Info` is a plain synchronous
`Console.WriteLine` (`Log.cs`). Synchronous console I/O on the simulation thread blocks ~50 ms per
call (worse with a real console window / QuickEdit selection), freezing the tick and delaying every
client's snapshot at once ⇒ **simultaneous, ~10 s-cadence stutter on all clients.** The same path
fires on connect/disconnect (PollEvents runs on the tick thread) — the login-time hitch.

Lesson: **never do synchronous `Console`/file I/O on the tick or network-callback thread.** Fix is
async logging (background consumer; tick thread only enqueues). Tracked in `todo/S24`. Also a
process lesson: **the synthetic stress tool masked this — reproduce perf issues with a real client
run, not only the stress harness.**

## The residual was the CLIENT, not the server (S25 → S26)

After the server was provably clean (S24: 0 tick_hitches with live clients), the human STILL
perceived stutter. S25 added client-side frame instrumentation (`frame_hitch` trace + on-screen
`FRAME` overlay: frame ms/max, hitch count, client GC deltas) and low-risk mitigations (overlay
throttle, `SetTextIfChanged`, concurrent GC). The overlay then nailed it: `FRAME ms=16.7/146.7
hitches=159 gc=6/1/0` — **159 frame hitches with only 6/1/0 GC collections, so NOT GC**, and the
worst frame was 146 ms. The bursty `tile_confirmed` interpolation trace (uneven 134–300 ms arrivals,
growing queueDepth) was a downstream symptom: uneven frames → uneven `Poll`.

Root cause: the Godot client uses the **Forward+ renderer (D3D12)**, whose lazy shader/pipeline
compilation hitches on first use of each material/mesh/light combo. Fix in `todo/S26`: switch to the
Compatibility/Mobile renderer (right for a 2.5D tile game) and/or precompile + reuse materials.

Lesson reinforced: this whole multi-round saga repeatedly **passed metrics but failed the human
check**. Instrument the layer the human actually experiences (here, client frame timing) before
fixing, and treat "the stress numbers look fine" as necessary-but-not-sufficient.

## Footgun avoided

Direct wire encoding was chosen over object pooling for hot messages deliberately: it removes the
allocation without the pool-lifetime / `ArrayPool` use-after-return / `Span`-escaping risks. Buffer
reuse is safe because LiteNetLib `Send` copies the payload synchronously (proven: 0 badPackets over
stress). If anyone later adds pooling here, that safety argument no longer holds automatically.
