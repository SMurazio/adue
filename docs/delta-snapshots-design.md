# Delta Snapshots Design (acked-baseline + delta encoding)

Design + decision record. Decided 2026-06-19 after the capacity study showed the per-channel cap is
**bandwidth-bound in dense (high visible-density) scenes** (dense 500 ≈ 24 Mbps out, ~28% tick budget;
see `capacity-ladder-study.md`). Delta snapshots are the roadmap-endorsed lever (networking-design-plan
Phase 2): the gate was "full snapshots well-measured + ack baseline exists" — both true now.

## Current model (what we change)

- Per tick, the replication step sends each viewer the **visible entities whose `StateRevision` differs
  from the revision last *sent* to that viewer** (`ClientSession._sentEntityRevisions` /
  `HasSentRevision` / `RememberSentRevision`). Plus a periodic **full heartbeat** (`ShouldSendFullSnapshot`,
  staggered by `NetworkId`) that resends *all* visible entities.
- Snapshots are **unreliable** (droppable). `SnapshotAck` is received but **ignored**
  (`AcknowledgeSnapshot` only records `LastAcknowledgedSnapshotSequence`).
- Consequence: a dropped snapshot desyncs the viewer (server marked entities "sent") until the next full
  heartbeat. So heartbeats are **required for correctness** — and in dense scenes resending ~120 visible
  entities to ~500 viewers periodically is the bandwidth (and the tick-tail spikes) that binds the cap.

## Target model — two coupled changes

1. **Baseline off the ACK, not the send.** Track, per viewer, the entity revisions the client has
   **acknowledged** (not merely been sent). Send an entity when its current revision differs from the
   client's **acked** revision. A dropped snapshot is never acked, so its changes are simply re-included
   next tick → **self-healing under loss**. This removes the need for the periodic full heartbeat
   entirely (a viewer's acked baseline + AOI-entry full-spawn is the resync).
2. **Delta-encode the per-entity payload.** Movement is single tile-steps, so encode "stepped in
   direction D" (~1 byte) instead of absolute `int16 x,y` (~4 bytes), plus a changed-field bitmask for
   facing/depleted. Halves the dominant dense cost. **Only safe on top of (1)** — a lost position-delta
   would permanently corrupt the client's position without the acked baseline to re-send it.

## Staging (each stage ships, is reviewed, and is measured independently)

**Stage 1 — acked-baseline delta + drop heartbeats (S46). Coords stay absolute.**
- Map snapshot-seq acks → per-entity acked revisions: when entity is sent at revision R in snapshot seq
  S, record it pending; when the client acks seq ≥ S, advance that entity's acked revision to R. Send an
  entity iff `currentRevision != ackedRevision`.
- AOI entry still sends the entity's full state (establishes the baseline for that viewer). AOI exit
  forgets it (already handled: `ForgetSentRevision` on despawn — extend to the acked/pending tracking).
- **Drop the periodic full heartbeat.** Keep a bounded safety: if a viewer has un-acked changes older
  than a threshold (wedged/silent client), force a fresh full re-baseline for it (or disconnect on a
  larger threshold) so per-viewer pending state can't grow unbounded.
- Encoding unchanged (absolute coords) — this stage is about the **baseline + loss-robustness**, not the
  wire size. Bandwidth win here = no more heartbeats; correctness win = no desync under loss.
- Protocol: keep `WorldSnapshot`/ack shape; the change is server-side selection + dropping the
  heartbeat. Bump version only if the wire actually changes.

**Stage 2 — delta-coded entity encoding (S47, depends on S46).**
- Per-entity: changed-field bitmask + step-delta position (direction byte) for moves; absolute only on
  baseline/AOI-entry. Protocol version bump. The big dense-bandwidth win.

## Correctness requirements (the bar — desync is silent and severe)

- **Convergence under loss:** with simulated packet loss, a client's reconstructed entity set/positions
  **must converge to the server's** (drop some snapshots in a test, then assert the client catches up
  once acks resume). This is the must-have test.
- **AOI invariant preserved:** outside-AOI entities are still never serialized to a viewer.
- **No unbounded per-viewer state:** the pending/acked bookkeeping is bounded (safety re-baseline).
- **Measured win:** re-run the **dense** ladder (central spawn, 500) and show outbound bandwidth + the
  heartbeat tail spikes drop vs the pre-delta numbers, gc still 0, AOI/tick budget healthy.

## Risks

- Hot path + correctness-critical: a subtle baseline bug silently desyncs clients (wrong positions, ghost
  entities) and may only show under loss. Lean hard on the convergence-under-loss test.
- Ack→entity mapping bookkeeping (which entities a given snapshot seq carried, per viewer) must be cheap
  and bounded — don't reintroduce per-tick allocation/GC on the hot path.
- Interaction with AOI hysteresis + spawn/despawn (S34's re-entry fix) — re-entry must re-baseline cleanly.
