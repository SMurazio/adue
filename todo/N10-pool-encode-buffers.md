# N10 — Pool the encode path to close the GC tail (flatter ticks at scale)

Severity: nit-tier (perf; metrics-gated). Not blocking — tick max is under budget at 120.

## Problem

S7 removed per-tick allocation from the AOI/visible/payload selection (good, verified no aliasing),
but the **dominant remaining per-tick allocator is the encode path**: `ProtocolCodec.Encode` builds
a fresh `MemoryStream` + `BinaryWriter` + `ToArray()` byte[] for every packet and every message,
and the server allocates tens of thousands of small message objects per minute (snapshots + the
~61k `EntityDespawn` from N9). This GC pressure is the tail behind the residual `tickMs` spikes
(~25–38 ms max across runs, vs ~3.5 ms average). It's under the 50 ms budget at 120 clients but
scales poorly.

## Fix (do when profiling/scale justifies — measure first)

- Reuse encode buffers: a pooled/threadlocal `MemoryStream`+`BinaryWriter` (or a `byte[]` buffer
  writer) reused across the single-threaded tick, instead of allocating per packet.
- Reduce message-object allocation on the hot path (snapshot/despawn), e.g. encode directly into the
  reusable buffer without intermediate message records where practical.
- Reducing despawn churn (N9) also cuts this allocation source — do N9 first; it may be enough.

## Acceptance

- A 120-client/60s stress run shows `tickMs max` consistently close to the average (the periodic GC
  spike largely gone), reported before/after.
- Behavior unchanged; `run-checks.cmd` green.

Per the design plan, this stays metrics-gated: only invest here when the tick budget actually
demands it (e.g. pushing client counts past ~150).
