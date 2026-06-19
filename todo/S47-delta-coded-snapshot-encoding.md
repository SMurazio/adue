# S47 — Delta-coded snapshot encoding (Stage 2 of delta snapshots)

> **ATTEMPTED AS S47b AND REVERTED (commit `c5bac3c`, 2026-06-19).** It shipped, passed the suite + a
> dense stress (~30% bandwidth win), and then **broke in live play**: the local player's client position
> **drifted ~4 tiles** from the server (confirmed via two clients + `client_entities`), which surfaced as
> "Too far from the node" even while standing *on* a resource. Root cause: **step-deltas are cumulative**,
> and the client applies snapshots in strict sequence order dropping any out-of-order/duplicate (MmoClient
> `HandleSnapshot` line ~359) — so **any dropped or reordered snapshot makes every subsequent step apply
> against the wrong base**, and the position stays drifted until the server happens to send an absolute
> re-baseline. Drops/reorders happen **even on loopback** under socket-buffer/poll pressure. Absolute
> coords (v15) cannot drift, so we reverted to them.
>
> **REDO REQUIREMENT (do NOT re-attempt without this):** a regression test that moves an entity
> **continuously over many steps under intermittent drop AND reorder** (not the single-gap convergence
> test S47a had — that always resynced before drift accumulated) and asserts the client — **especially the
> local player** — never drifts from the server. Also reconsider the design: either (a) make the client
> resync cheaply whenever it detects a sequence gap (request/await an absolute, don't apply a step across a
> gap), or (b) cap drift by sending absolute far more often, or (c) drop step-deltas entirely and pursue
> the dense-bandwidth win another way (the packed-crowd cost is O(N²) regardless — see the S40 density
> decision; this win may not be worth the fragility). The ~30% was lossless but **not worth a live desync**.

Severity: should-fix (scaling). Stage 2 — the **big dense-bandwidth cut** that S46 made safe. Design:
`docs/delta-snapshots-design.md`. **Depends on S46 AND S47a** (highest-contiguous ack — extracted into its
own task). Do NOT start until S47a lands: step-deltas are cumulative, so a baseline that can silently skip
(the S46 reorder gap) would **permanently corrupt** positions. Hot path + correctness-critical; the
convergence-under-loss gap test is the bar.

## What (the encoding)

1. **Protocol bump (from v15):** per-entity, send a **changed-field bitmask** + only the changed fields.
   For position, since movement is single tile-steps, encode a **step delta** (one `Direction8` byte)
   when the entity moved exactly one tile from its baseline; fall back to absolute coords on
   baseline/AOI-entry or a non-unit move (teleport/spawn). Facing/depleted ride the bitmask.
2. **Baseline-relative:** deltas are encoded against the entity's **acked** baseline state (from S46), so
   the client always has the referenced baseline (the contiguous-ack guarantee makes this sound).
   AOI-entry / re-baseline / force-rebaseline send **absolute** (establish the baseline).
3. **Client decode:** apply the bitmask + step-delta against its current value; absolute on baseline.
4. Keep it allocation-light on the hot path (no per-tick GC).

## Files
- `src/Mmo.Shared/Protocol/` — `EntityStateSnapshot`/`WorldSnapshot` wire encoding (bitmask + step-delta),
  codec read/write, version bump.
- `src/Mmo.Server/Runtime/` — emit deltas vs the acked baseline; absolute on baseline/entry; the
  highest-contiguous-ack handling (prerequisite).
- `src/Mmo.Client.Core/` — decode deltas; ack highest-contiguous; apply against current state.

## Tests
- **Convergence under loss, gap case (MUST):** drop arbitrary middle snapshots while entities step; assert
  the client's positions converge **exactly** to the server's (this is where step-deltas would corrupt
  without the contiguous-ack fix).
- Codec round-trips for bitmask + step-delta + absolute fallback (unit).
- AOI invariant + entry/exit/re-entry still correct; existing snapshot/movement/interact tests pass.

## Acceptance
- Per-moving-entity payload drops materially (≈8 → ≈3 bytes); a **dense** 500-client/30s central-spawn
  stress shows outbound bandwidth drop **substantially** vs the S46 dense numbers (~21.5 Mbps), gc 0,
  AOI/tick healthy, 0 errors. (Orchestrator runs it.)
- `run-checks.cmd` green; protocol version bumped. Do NOT commit — Orchestrator reviews.

## Notes
- **May split:** S47a = highest-contiguous-ack (the prerequisite correctness fix, small, no encoding) →
  S47b = the delta encoding. Recommended if S47 is too large for one reviewable unit.
- This is the lever that actually raises the bandwidth-bound dense cap (`capacity-ladder-study.md`).
