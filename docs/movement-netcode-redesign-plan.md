# Movement-Input Netcode Redesign — staged incremental plan

Status: **approved-to-plan; staged build pending go-ahead.** Branch `review/tile-step-todo`, protocol v22.
Unifies the parked timestamped-input replay spike (`timestamped-input-replay-spike.md`) + the live-but-broken
UO1–UO5 client-driven commit stream into ONE coherent model. Gated by TEST1
(`tests/Mmo.Client.Core.Tests/TimingFaithfulReconcileHarnessTests.cs`, headless feel) + clumsy (live loss/latency).

## Root cause (confirmed live with clumsy, local avatar GodotB)
Movement input is sent **`ReliableOrdered`**. Under loss that (1) head-of-line-blocks → freeze-then-jump, and
(2) retransmits arrive **bunched** → a burst of steps applies at once → the local avatar **speeds up, overshoots,
desyncs**. Same root as the UO5 frame-drop overshoot and the latency snapping. Snapshots already self-heal
(acked-baseline) — leave them.

## Target model (solves loss + latency together)
**Reliability via redundancy, not retransmission** + **authored-tick server replay** (Gambetta/Valve canon):
- Client sends **one** `MoveInputMessage`, **unreliable**, fixed-rate (~10–20Hz): full current intent state
  (redundant — a lost packet is superseded by the next) + a **sliding window of the last N sequenced inputs**
  (deltas, ~4 B each); server **dedupes by sequence**. No head-of-line stall, no retransmit bunching. A lost STOP
  is covered by re-sending current state, not a reliable channel.
- Server buffers inputs by **authored tick**, reconstructs the timeline (carry-forward = held-intent feel from
  events), and applies each at its authored tick via the existing deterministic `WorldEntity.TryStep`, gated by
  the cooldown (the anti-speedhack cap). Late input → bounded rollback + re-step.
- Result: server confirms the predicted timeline → reconcile collapses to a plain re-anchor; loss, latency, and
  the UO5 overshoot all resolve at once.

## Staged, revertable commits (each: one commit, TEST1-green, a named clumsy scenario, default mode stays playable)
1. **Stage 1 (S, FIRST):** redundant-unreliable delivery, **server still held-paced.** New `MoveInputMessage`
   (full-state redundancy + window), unreliable fixed-rate send replacing the reliable `SendMoveIntent` + the
   keepalive; server dedupes by seq and feeds the EXISTING held-intent stepper. No rollback/stepping-model
   change. *Clumsy: 10% loss + 100ms → freeze-then-jump gone.* TEST1 unchanged-green. Trivially revertable.
   **This is the recommended first step** — headline loss win, smallest surface, zero rollback risk.
2. **Stage 2 (M):** stamp authored `HeadTick` (`EstimateTick`, already computed) + server input ring + clamping
   (anti-cheat), **buffer only, not yet stepping from it.** De-risks the buffer/clamp before it drives sim.
3. **Stage 3 (M-L):** `WorldEntity.CaptureState/RestoreState` for ALL rollback fields + **`SpatialEntityGrid`
   bucket re-migration on restore** (the sneaky AOI hazard) + an AOI-integrity test. Pure API, no stepper change.
4. **Stage 4 (L, highest-risk):** replace `StepHeldMovementIntents` with the **authored-tick replay stepper**;
   late input → `RestoreState` + re-step (bounded by clamp). *Clumsy: 10% loss + 150ms + reorder → bunching/
   overshoot GONE; old UO5 frame-drop burst converges.* TEST1 Invariant 1 (no cap/snap) is the critical gate;
   extend the harness server model to drive the replay path.
5. **Stage 5 (M, mostly DELETION):** gut `Reconcile` to a plain re-anchor (delete `MaxInFlightLead`,
   `_clientDriven`, `_inFlightDir`, S85 re-arm); delete `StepCommitRequest`/`MovementMode`/`TryCommitStep`/
   `HandleStepCommit`/`ClientDrivenMovement`; collapse the A/B/D model duality to one model; delete
   `todo/UO5`. Version bump.

## Disposition of UO1–UO5 + S103 (net complexity DROPS)
- **DELETE:** UO1 `MovementMode` + `ClientDrivenMovement` (no pacing duality), UO1 per-step `StepCommitRequest`
  emit (→ the input window), UO3 `_clientDriven` uncapped re-projection, S103 `TryCommitStep` + borrow, the
  skew-compensation machinery (`MaxInFlightLead`/`_inFlightDir`/S85 re-arm).
- **SUPERSEDED — delete the todo:** **UO5.** Its root cause IS "the cooldown gate rejects an out-of-order burst";
  authored-tick replay dissolves it (the burst's authored ticks confirm at cadence). The todo's own candidate #3
  ("apply commits at their authored ClientTick") is this milestone. **So we do NOT separately re-attempt UO5.**
- **KEEP:** `WorldEntity.TryStep` + cooldown gate verbatim; predictor stepping + `CalibrateToServerTick`; the
  snapshot/AOI/`RecipientStepSeq` self-heal; the render tween; UO4 stop-on-reversal (re-verify); TEST1 (extended).

## Risks
1. **Spatial-index rollback hazard** — `Tile` mirrors into `SpatialEntityGrid` outside `WorldEntity` state; a naive
   restore desyncs AOI. Mitigated by making re-migration part of Stage 3's restore API + an integrity test,
   BEFORE Stage 4 calls it.
2. **Anti-cheat/DoS** — far-past authored ticks force deep rollbacks; clamp window + ring depth bound it; cooldown
   gate caps rate regardless. Less anti-cheat surface than today's deleted commit-borrow.
3. **CPU at 120–150 players** — per-entity rollback is O(players × ringDepth) worst case; rollback only fires on a
   genuinely late input (rare under redundant delivery). MUST profile with the stress harness before Stage 4.
4. **Held-intent → sequenced-input reversal** — reverses a past deliberate decision; the carry-forward ring keeps
   the held-intent server feel. Name it; update `movement-input-model.md`.

## Why now
The session's blocker was "can't verify movement feel." Now we have **clumsy** (real loss+latency) + **TEST1**
(headless feel). So each stage is verifiable both ways — this ends the patch whack-a-mole with a coherent model
that deletes more than it adds. Start with Stage 1.
