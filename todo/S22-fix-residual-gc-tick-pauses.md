# S22 — Fix residual GC tick-pause spikes (the remaining movement micro-slowdown)

Severity: should-fix. **User-prioritized — the slowdown is reduced after S21 but still happens, less
often.** Diagnosis is evidence-backed (see below), but confirm gen2 correlation before/after.

## Evidence (residual cause = GC, not scheduler)

- S21 fixed the frequent scheduler over-sleep (`driftMs avg/max` 7.85/20.08 → 0.02/4.10).
- Residual: S21 stress `tickMs max ≈ 33 ms` with `driftMs ≈ 0` (scheduler ruled out) and only ~13 ms
  in the measured budget buckets → **~20 ms unaccounted pause** on the worst tick.
- The server runs on **Workstation GC** — no `ServerGarbageCollection`/`ConcurrentGarbageCollection`
  in `Mmo.Server.csproj` or `Directory.Build.props`. Workstation Gen2 = blocking stop-the-world
  pauses (periodic ⇒ "less often").
- A 60s stress with the movement trace at the default threshold logged **0 tick_hitch** — the
  residual spikes are sub-75 ms (mid-tick GC pauses), not big inter-tick gaps.

## Part 1 — make it measurable (do first; the trace currently can't see it)

The S20 tick-hitch threshold (1.5× interval = 75 ms) misses 20–33 ms pauses. Add/refine a
**duration-based** trigger (e.g. log a tick whose `durationMs` exceeds ~10–15 ms) *separately* from
the inter-tick-gap trigger, so a low duration threshold doesn't flood on the normal ~50 ms gap. Then
a stress run surfaces the residual ticks with their `gc0/1/2` deltas → **confirm the slow ticks
correlate with a `gc2` (or `gc1`) bump.**

A 20 ms pause is large — it means significant per-tick garbage. Both fixes below matter; the
allocation one is the real cure, GC config is the safety net. Do GC config first (quick win, lets us
re-measure), then attack allocation hard.

## Part 2 — Server + background GC (quick win, do first)

Enable in `Mmo.Server.csproj` (or `Directory.Build.props`):
`<ServerGarbageCollection>true</ServerGarbageCollection>` and
`<ConcurrentGarbageCollection>true</ConcurrentGarbageCollection>`. Moves Gen2 work mostly
background/concurrent, cutting the blocking pause. Re-measure with the refined trace.

## Part 3 — reduce per-tick allocation (the real fix, NOT optional)

A 20 ms Gen2 pause means the hot path is minting too much garbage; kill it so collections become both
smaller and rarer. Targets on the server tick path: per-recipient snapshot/AOI building (temp lists,
LINQ, HashSets per recipient per tick — N10 pooled only the encode buffer), and per-tick message-
object churn (e.g. ~21k `EntityDespawn` + spawn/chat records/min at 120 players → pool/reuse or
encode without per-message record allocation). Profile allocations (GC alloc counters / a sampling
profiler) to find the biggest per-tick sources, then apply the standard toolkit:

- **Reuse/pool** instead of per-tick `new`: reused scratch lists/sets, `ArrayPool<T>.Shared` for
  transient buffers, small object pools for hot message types.
- **`Span<T>`/`ReadOnlySpan<T>`** for slicing/encoding without intermediate arrays; `stackalloc` for
  small fixed temp buffers.
- **`struct`** for tiny short-lived values; avoid boxing.
- **Remove LINQ / closures** from the per-tick path — they allocate enumerators + captured-variable
  closures. Plain loops over reused buffers instead.

**GUARDRAILS (do not skip):** apply this ONLY to the per-tick hot path the profiler/trace flags —
NOT cold paths (login, chat, startup, migrations); zero-allocation everywhere is premature
optimization. Watch the footguns: `ref struct` lifetimes, `Span` escaping scope, `ArrayPool`
use-after-return / missing returns, `stackalloc` size limits. Measure `tickMs`/Gen2 after EACH
change — keep only changes that move the numbers; revert ones that don't.

## Acceptance (measure before/after)

- With the refined trace (Part 1): confirm pre-fix slow ticks correlate with `gc2` bumps; report
  Gen0/1/2 collection counts over a 60s stress before vs after.
- After the fix: a 120-client/60s stress shows `tickMs max` **well under ~5 ms** (no recurring
  20–33 ms pauses) and a large drop in Gen2 collection count. Report before/after `tickMs max` +
  GC counts.
- **Human re-check:** 2-client Godot movement is smooth — no perceptible hitching.
- `run-checks.cmd` green.

## Blocked

Implemented the measurable/debuggable portion, server/concurrent GC, and several hot-path allocation
reductions, but the full acceptance criterion is not met yet.

Measured before these changes with the refined metrics:

- 120-client/60s stress: `tickMs avg/max=4.61/35.38`, `driftMs avg/max=0.02/5.92`,
  `gc=39/3/2`, budget avg `move/aoi/ser/net/persist/other=0.05/2.76/0.19/0.48/0.00/0.00`.

Measured after these changes:

- 120-client/60s stress: `tickMs avg/max=2.19/33.57`, `driftMs avg/max=0.02/4.26`,
  `gc=0/0/0`, budget avg `move/aoi/ser/net/persist/other=0.04/0.85/0.15/0.38/0.00/0.00`,
  budget max `1.98/2.88/1.02/2.13/1.88/0.46`, faults/errors `0/0`.
- 5s live window during the same run still had `tickMs max=21.86` with `gc=0/0/0`.

So the GC-side mitigation worked: Gen0/1/2 dropped to zero during the stress run and average tick
time dropped sharply. The remaining max tick is not GC-correlated and is mostly outside the current
budget buckets, so the task's "tickMs max well under ~5 ms" acceptance still fails. This needs an
Orchestrator decision for the next step: deeper profiling/tracing of unbudgeted runtime pauses,
benchmarking Release vs Debug explicitly, or revising the acceptance to account for OS/runtime
outliers. The required human Godot smoothness re-check also remains manual.
