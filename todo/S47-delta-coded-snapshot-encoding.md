# S47 — Delta-coded snapshot encoding (Stage 2 of delta snapshots)

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
