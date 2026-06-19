# Ability Telegraph Synchronization (design note — for the future combat phase)

Status: **design note, not implemented.** No combat/abilities exist yet. Captured now because it's the
spine of telegraphed abilities and it slots cleanly into our replication model. Prompted by a play-test
design discussion (2026-06-19).

## Problem
A telegraphed ability (boss AoE warning, ground-targeted spell, charged attack) should **resolve at the
same wall-clock instant on every client's screen** — the hit lands exactly when the telegraph fills — even
though each client has a different, variable latency to the server.

## Approach: server-authoritative resolve-tick + a shared synced clock
Do **not** hand-compute a per-client telegraph duration (`K + latency` for the caster, `K − latency` for
observers). Flip it into a **shared deadline**:

1. The server schedules the ability and stamps an **absolute resolution time** — "resolves at **server-tick
   T**" (T = now + K, K = nominal telegraph length).
2. The server broadcasts the telegraph event to AOI viewers: `{ability, origin, shape, resolveTick T}`
   (reliable).
3. Every client renders the fill as `progress = (now − start) / (T − start)` in **synced server-time** and
   triggers the resolve visual when its clock reaches T.

All clients land on T at the same wall-clock instant. Each client's *local* telegraph duration
(`T − when_it_started_showing`) works out to caster-long / far-observer-short **automatically** — the
latency compensation falls out of counting down to a shared deadline; no per-client latency estimate, no
+/− sign to get right.

### Why the deadline form beats explicit `K ± latency`
- **Asymmetric / jittery latency:** counting down to T doesn't assume one stable, symmetric RTT.
- **Late joiners are free:** a client entering AOI mid-telegraph gets T and renders the correct *remaining*
  fill; the per-duration scheme has nothing to hand it.
- **No per-client server work:** the server stamps T once; each client does its own arithmetic.

### The hard constraint
**K must be ≥ the worst-case one-way latency**, or a laggy client receives the event after T (the literal
`K − latency` goes negative — "no telegraph, already hit"). This is *why* real telegraphs are deliberately
long (~1.5–3 s): the wind-up is sized so even the laggiest player gets a fair, dodgeable warning.

## The crucial split: synced visual vs authoritative outcome
- The telegraph **visual** is synchronized cosmetics (the deadline trick).
- **Who actually gets hit is server-authoritative, decided at tick T from entity positions at tick T.**
  Never predict the outcome — that is exactly what lets players dodge (the server checks where you are when
  it resolves, not where you were when it started).
- The caster may render its own telegraph instantly on keypress (predicted start) for responsiveness, then
  nudge its fill-rate to land on the server's confirmed T — same predict-then-reconcile idea as movement
  (see [networking-design-plan.md](networking-design-plan.md) §2 and the S53 prediction work).

## Fit to our architecture
This is just **"schedule a future server-authoritative event and broadcast its resolve-tick to AOI"** —
native to our replication model. Pieces needed when combat lands:
- An **ability/telegraph event** in the protocol carrying the resolve-tick (+ shape/origin), reliable, AOI-
  scoped — like spawn/despawn.
- **Server-side scheduled resolution** at tick T: gather affected entities by the telegraph shape at T,
  apply effects authoritatively.
- **Lightweight client clock/tick sync:** each client estimates `serverTick ≈ local + offset` from the RTT
  LiteNetLib already exposes. We already ship a server tick in every snapshot, so we're most of the way
  there — formalize a small offset estimator with smoothing.

## Not now
No work until combat is on the roadmap. This note is the reference for that phase.
