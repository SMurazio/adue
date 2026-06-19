# S47a — Highest-contiguous snapshot ack (close the reorder/gap baseline skip)

Severity: should-fix (correctness). Prerequisite for S47b (delta encoding). Small, pure-correctness, no
encoding. See `docs/delta-snapshots-design.md` and the S46 commit's flagged gap.

## Problem (flagged during S46 review)

S46's acked baseline advances per acked snapshot **sequence**. But the client currently acks the
**latest snapshot it received** (`SnapshotAck(snapshot.SnapshotSequence)`), and the server advances the
baseline for every pending record with `sequence <= acked`. With **UDP loss + reorder** this skips:
a snapshot carrying entity X (revision R) is **dropped**, a *later* snapshot (not carrying X) **arrives
and is acked**, and the server advances X's acked revision to R even though the client never got it.

With S46's **absolute** coords this self-corrects on X's next change (tolerable). But S47b's
**step-deltas are cumulative**, so a skipped delta would **permanently corrupt** X's position. Fix the
ack model first, in isolation.

## What

1. **Client acks the highest *contiguously-received* sequence**, not the latest. Track which snapshot
   sequences have arrived; ack the top of the **gap-free prefix** (e.g. received 1,2,3,5,6 with 4 missing
   → ack 3). A dropped sequence stalls the ack at the gap until it's filled, so the server never advances
   the baseline past a gap. Primary change: `src/Mmo.Client.Core/` snapshot handling (`MmoClient`).
   (Reuses the existing `SnapshotAck` field — **no wire change** expected.)
2. **Server:** confirm `AcknowledgeSnapshot` advancing all records `<= acked` is now sound (no gaps below
   a contiguous ack). No change expected beyond confirming the invariant; adjust only if needed.
3. **Confirm the S46 safety interaction:** a permanently-lost sequence stalls the contiguous ack →
   `_oldestUnackedSentTick` ages → the **2 s force-re-baseline** resyncs (sends a complete snapshot,
   resets the baseline). Verify this recovers a stuck prefix rather than stalling forever.
4. (Optional) update the synthetic stress clients to ack contiguous too — but they run on loopback (no
   loss) so latest == contiguous there; skip unless trivial.

## Tests
- **Gap convergence (the case S46 couldn't cover):** in an integration test, deliver snapshots with a
  **middle one dropped** while an entity steps, and assert the entity is **not** marked acked past the
  gap and the client converges exactly once the gap is filled (or after the 2 s re-baseline). This is
  distinct from S46's "drop ALL then resume" test.
- Highest-contiguous computation unit test (received-set with gaps → correct ack).
- No-loss path unchanged (contiguous == latest); existing snapshot/AOI/movement/interact tests pass.

## Acceptance
- Under reordered/lossy delivery the server never advances an entity's acked baseline past a sequence the
  client did not contiguously receive; the client converges (directly or via the 2 s re-baseline).
- `run-checks.cmd` green. No wire/protocol change (or a documented minimal one). Do NOT commit —
  Orchestrator reviews. (No bandwidth change expected here — this is the correctness gate that makes S47b
  safe.)
