# S83 — Authoritative-while-moving reconcile (re-anchor + re-project in-flight). Fixes the turn desync at root.

Implements **option 1** from the independent review (`review/review-request-movement-independent-review.md` —
READ IT FIRST; it has the root-cause diagnosis + the design). Decision made by the human: keep prediction,
fix the reconcile. Client-core only. Base: current HEAD (S82 already reverted).

## Root cause (from the review — the thing all prior fixes missed)
The predictor flips its held direction INSTANTLY on input (`SetIntent`), but the server only sees that
direction **one+ tick later** (the intent message crosses the wire + lands in the server's next poll). So at a
turn, predictor-tick-N and server-tick-N are fed DIFFERENT inputs and make different turn-vs-step decisions →
diverge a tile per turn; rapid turns compound it. It never recovers while moving because the only real
convergence path is gated on `!_moving`. "0 latency" is a red herring: snapshot jitter is ~0 on LAN but
**input→server delay is never 0**, and the predictor models input as instantaneous. CRITICAL: every existing
parity test feeds BOTH sides the SAME input timeline, which is why they pass while the live client desyncs.

## What — the standard client-prediction reconciliation
Make `Reconcile` authoritative **while moving** (drop the `!_moving` gating and the match-vs-mismatch
heuristic): on each confirming snapshot, **re-anchor the predicted position to the latest confirmed tile, then
re-project ONLY the genuinely in-flight steps** (the held-intent inputs the client has predicted past the
server's confirmed point) forward from that anchor to recompute the present predicted tile. This is the
textbook "re-base on authoritative state + replay un-acked inputs" model — it caps divergence at ALL times (so
spam can't ratchet) and converges to the server whenever input pauses. Unlike the reverted S82 it does NOT
withhold/hold requested steps — it lets prediction run and corrects after the fact (which is why S82 felt
worse and this won't).
- RENDER must glide to the **re-projected present** tile, NOT to the bare (trailing) confirmed tile — re-anchor
  is internal; the visible avatar goes to confirmed+in-flight = present. Do not reintroduce a backward
  rubberband (blend the render from where it shows now to the re-projected present over one cadence).
- Keep the S81 tick-grid predictor STEPPING (the review says S81 fixed a real, separate predictor-internal
  clock bug). Simplify/replace only the reconcile path. The S76 step-seq is available to identify the in-flight
  count; use it or the confirmed tile as the anchor — your call, justify it. Do NOT re-land S82's lead bound.

## Tests — the gate that actually catches this (prior tests could not)
- **NEW parity test with SKEWED inputs:** drive the predictor and the REAL `WorldEntity.TryStep` where the
  predictor receives each direction change at tick N but the server receives it at tick N+1 (model the
  one-tick input-arrival delay), through rapid down/left (and E/W) turn spam. Assert the reconciled predicted
  tile stays within a bounded divergence during spam AND converges exactly to the server tile once input stops.
  This MUST fail on the current `!_moving`-gated reconcile and pass after. (The existing same-timeline parity
  sweeps stay green — but add this one because it's the only test that exercises the real skew.)
- Keep the S81 OffGrid sweep + S77 reconcile tests green or consciously update any whose semantics the new
  authoritative reconcile changes (e.g. anything asserting the old match/idle-clause behaviour) — explain each
  change.

## Constraints
- Client-core only; no server/protocol change. Server stopped (dev mode). Run
  `.\.shared\skills\mmo-dev\scripts\run-checks.cmd` before/after (try it; if Bash denied, note + continue —
  Orchestrator runs the gate + a LIVE overlay re-test: spam down/left, gap must stay small + close at rest, and
  it must NOT stutter/withhold like S82). You can't run Godot. **Safe Local Execution** binds you. Do NOT
  commit, delete the task file, or push. If you find the review's option-1 can't be made to both bound
  divergence AND avoid a visible rubberband, STOP and surface it rather than guessing.

## Acceptance
- The skewed-input reconcile test fails-before/passes-after; divergence bounded during spam and converges to
  the server at rest; render glides to the re-projected present (no backward rubberband, no S82-style
  withholding); `run-checks` green. Review-request → `review/review-request-s83-authoritative-reconcile.md`.
  Do NOT commit or delete the task file.
