# S46 — Acked-baseline delta snapshots, drop full heartbeats (Stage 1)

Severity: should-fix (scaling). Stage 1 of delta snapshots — the lever for the **bandwidth-bound dense
cap** (capacity study). Full design: `docs/delta-snapshots-design.md`. **Correctness-critical hot path** —
a subtle bug silently desyncs clients (wrong positions / ghost entities), so the convergence-under-loss
test is the bar. Stage 2 (delta-coded encoding) is a separate follow-up (S47) that builds on this.

## Scope (Stage 1 = baseline + loss-robustness; coords stay ABSOLUTE; no wire-size change yet)

1. **Use the ack to define the baseline.** Today the server sends entities whose `StateRevision` differs
   from what it last *sent* (`ClientSession._sentEntityRevisions`), with a periodic full heartbeat to
   recover from drops; `SnapshotAck` is recorded but ignored. Change selection to send an entity iff its
   current revision differs from the revision the client has **acknowledged**:
   - Track, per viewer, what each outgoing snapshot **sequence** carried (entity → revision), and on
     `AcknowledgeSnapshot(seq)` advance each carried entity's **acked revision** for all seqs ≤ acked.
   - A dropped snapshot is never acked, so its entities stay "unacked" and are re-included next tick →
     self-healing under loss. Keep this bookkeeping **bounded and allocation-light** (no per-tick GC).
2. **Drop the periodic full heartbeat** (`ShouldSendFullSnapshot`/the heartbeat path). AOI **entry** still
   sends the entity's full state (establishes the per-viewer baseline); AOI **exit** forgets its
   acked/pending state (extend the existing `ForgetSentRevision`-on-despawn path). Re-entry (S34) must
   re-baseline cleanly.
3. **Safety bound:** if a viewer has changes unacked beyond a threshold (wedged/silent client), force a
   fresh full re-baseline for that viewer (and/or disconnect on a larger threshold) so per-viewer pending
   state cannot grow unbounded.
4. Keep encoding **absolute** (no step-delta yet — that's S47). Only bump the protocol version if the wire
   actually changes (this stage may not need to).

## Files (server-side replication; minimal/none on the wire)
- `src/Mmo.Server/Runtime/ClientSession.cs` — acked-revision + per-seq carried-entity bookkeeping (replace
  or augment `_sentEntityRevisions`); the safety threshold.
- `src/Mmo.Server/Runtime/GameServer.cs` — snapshot selection uses acked baseline; remove the heartbeat
  full-resend; AOI entry/exit baseline establishment + forget.
- Client (`src/Mmo.Client.Core/`) — only if anything changes in how it merges/acks (acks already sent).

## Tests (the Orchestrator runs them)
- **Convergence under loss (MUST-HAVE):** in an integration test, drop/withhold some snapshots (or acks),
  keep mutating entities, then resume acks — assert the client's reconstructed entity set + tiles
  **converge exactly** to the server's. Without heartbeats, this proves the acked-baseline self-heals.
- **No-loss steady state:** client stays in sync tick-to-tick (parity with today, minus heartbeats).
- **AOI invariant** still holds (outside-AOI never serialized); AOI exit→re-entry re-baselines (extends the
  S34 re-entry test).
- Safety bound: a viewer that never acks triggers a bounded re-baseline (not unbounded growth).
- Existing snapshot/AOI/movement/interact integration tests still pass.

## Acceptance
- Clients stay in sync **without** periodic full heartbeats, including **under simulated packet loss**
  (convergence test green).
- `run-checks.cmd` green. A **dense** 120-client/30s stress (central spawn) shows outbound bandwidth + the
  heartbeat-driven tick-tail spikes **drop** vs the pre-delta dense numbers, gc still 0, AOI/tick healthy.
  (Orchestrator runs the dense stress.) Do NOT commit — Orchestrator reviews.

## Notes
- This is the risky structural half done in isolation with the existing test suite as the safety net —
  no encoding change, so wins are "heartbeats gone + loss-robust," and a desync bug shows up as a failed
  convergence/parity test, not silently.
- Stage 2 (S47): delta-coded per-entity encoding (step-delta positions + changed-field bitmask) for the
  big dense-bandwidth cut — only safe on top of this stage's acked baseline.
