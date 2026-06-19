# S47 — Delta-coded snapshot encoding (Stage 2 of delta snapshots)

Severity: should-fix (scaling). Stage 2 — the **big dense-bandwidth cut** that S46 made safe. Design:
`docs/delta-snapshots-design.md`. **Depends on S46.** Hot path + correctness-critical: a wrong delta
**permanently corrupts** client positions (deltas are cumulative), so the two prerequisites below are
mandatory, and the convergence-under-loss test is the bar.

## Prerequisite (MUST do first — S46 flagged it): highest-contiguous ack

S46's baseline can be silently skipped by **cumulative ack + UDP reorder**: a dropped snapshot carrying
entity X (revision R) followed by a *later* acked snapshot (not carrying X) advances X's acked revision
to R even though the client never received it. With **absolute** coords (S46) this self-corrects on X's
next change; with **step-deltas** it permanently desyncs X's position. Fix before encoding:
- Client acks the **highest contiguously-received** snapshot sequence (track received seqs; ack the top
  of the gap-free prefix), not the latest-received. Then the server never advances the baseline past a
  gap. (Reuses the existing `SnapshotAck` field — likely **no wire change** for the ack itself.)
- The S46 2-s force-re-baseline already bounds a stuck prefix (a permanently-lost seq stalls the
  contiguous ack → re-baseline resyncs). Confirm that interaction.
- Test: drop a MIDDLE snapshot (not all), keep a later one, and assert the entity is NOT marked acked /
  converges — i.e. the gap case S46 couldn't cover.

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
