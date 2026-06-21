# Design Spike: Held-Intent → Timestamped-Input + Server-Replay (latency fix)

Status: **research/decision-support only — no production code committed.** Tile-stepped game frozen + tagged
`tile-stepped-stable`. Pairs (a) the Gambetta/Valve-Source canon from our own `networking-reference-catalogue.md`
with (b) a grounded lift estimate of *this* codebase.

## The problem (confirmed on the live build, ~100 ms)
Both render models feel bad at latency: **model B holds** (1-tile cosmetic lead can't cover the gap →
glide-one-tile-then-wait stutter), **model A snaps** (it predicts forward, but the server acts on a *different*
input timeline). The root cause is one thing: the server uses **"latest held direction at tick time"**
(`GameServer.StepHeldMovementIntents`), so it does NOT reproduce the client's exact input *timeline* — predictor-
tick-N and server-tick-N make different decisions, and the gap shows as snap/hold proportional to RTT.

## The fix (Gambetta / Valve-Source "process commands at their authored time")
Client **timestamps each input** with the server-tick it was authored at. Server keeps a small per-entity
**rolling input buffer + state ring (4–8 deep)** and applies each input **at its authored tick**, **rolling back
and re-stepping the entity** if a late input lands before the already-simulated tick. Because the server then
runs the *identical* inputs through the *identical* step logic the client predicted, **the server's tiles match
the prediction ⇒ reconcile collapses ⇒ no snap, no hold.** Anti-cheat survives: the cooldown gate caps step rate
regardless of claimed timestamps; clamp timestamps to a recent window.

## Why this fits US unusually well (the key finding)
The single hardest requirement for rollback-replay — **a step that's a pure deterministic function of
`(direction, tick, cooldown, grid)` with no dependence on other entities** — is **already met**, and was built
deliberately for the S81/S98 predictor-parity work:
- `WorldEntity.TryStep` is deterministic; collision is the *static* blocked-tile map only; no other entity is
  consulted. A rollback is "re-step this one entity a few tiles against static collision" — per-entity, cheap.
- The client **already mirrors that exact step logic** (`LocalPlayerPredictor.Tick`/`AdvanceOneStep`), proven
  tick-for-tick by the parity tests. So "the server runs what the client predicted" is *already true at 0 ms* —
  the only thing wrong is input *timing*.
- A **shared client↔server tick clock already exists** (`LocalPlayerPredictor.CalibrateToServerTick`, anchored on
  `snapshot.ServerTick`) — exactly what you author timestamps against. It's computed but never sent back.

So this is **"change *when* inputs are applied," not "rebuild movement."** That's why it's a smaller lift than the
continuous-movement migration: the deterministic, agreed simulation is the expensive part, and it's done + tested.

## Lift estimate (L–XL, but a lot is deletion)
| Phase | Scope | Size |
|---|---|---|
| 0 | Throwaway spike — prove prediction stops snapping at 100–150 ms | **S** |
| 1 | Protocol: add `ClientTick` to the intent (hybrid) or new `MoveInputMessage`; version bump; both clients' send paths | M |
| 2 | Server input buffer + per-tick state ring + new `WorldEntity` save/restore API (the 3 private gate fields) | M–L |
| 3 | Rollback/replay driver (replace `StepHeldMovementIntents`); late-input restore+re-step | L |
| 4 | Timestamp validation/clamping (anti-cheat window, bound rollback depth) | M |
| 5 | Simplify reconcile + delete now-dead paths (skew machinery; likely the S103 commit-step + the A/B model duality) | M (mostly deletion) |

**Replacement, not a parallel mode** at the server (one stepping model), though the spike is fully parallel.

**Reused (the bulk):** `TryStep` logic verbatim, the client/server parity mirror, the cooldown anti-speedhack
gate, the snapshot/delta/AOI framework, the `RecipientStepSeq` reconcile philosophy, the render tween.

**Throwaway / deleted (a feature, not a cost):** `StepHeldMovementIntents`, the held-intent-only intent shape,
the reconcile skew-compensation machinery (`MaxInFlightLead`, `_inFlightDir` re-projection, the S85 re-arm —
these exist *only* to paper over the skew this removes), and — notably — **the entire S103 commit-step subsystem
and the A/B model-B-vs-A duality**, both of which exist *only because neither model felt right at latency*. If
replay fixes that, they're solving a problem that no longer exists. Net: this likely **removes** more complexity
than it adds.

## Highest risks
1. **Spatial-index rollback hazard (sneaky).** `Tile` is mirrored into `SpatialEntityGrid` via
   `Zone.OnEntityMoved`, *outside* `WorldEntity`'s state. A naive save/restore of the 5 entity fields silently
   desyncs AOI on every rollback — the replay must re-migrate buckets. Also: `_nextEligibleTick`/`_lastStepTick`
   are private (write-only via `TryStep`) → need a new restore API. `StepSequence` must land exactly where a
   clean sim would, or `RecipientStepSeq` reconcile breaks.
2. **Anti-cheat timestamp validation (new surface).** Cooldown caps rate, but far-past timestamps could force
   expensive rollbacks (DoS-ish) — clamp window + ring depth mitigate, but tuning vs the ±1-tick clock jitter is
   fiddly.
3. **CPU at 120–150 players.** Per-entity rollback is bounded (`O(players × ringDepth)` extra `TryStep` worst
   case), and movement is already a measured budget category — but it's a real regression vs today's exactly-one-
   step-per-entity-per-tick. **Must verify with the existing stress harness.**
4. **Architectural reversal.** `docs/movement-input-model.md` deliberately moved *away* from sequenced per-step
   events toward held-intent. This moves back toward authored events (keepalive still needed as a clock
   heartbeat + wedged-client timeout). Name it explicitly.

## Recommended next step: the spike (Phase 0, ~S, throwaway, on a branch)
Answer the only question that justifies the L–XL spend: **does prediction stop snapping at 100–150 ms?**
- Add `ClientTick` to the intent on the wire (two codec writes), carrying `EstimateTick(now)` from the existing
  calibration.
- Server, behind a flag, for ONE session: minimal timeline-replay (buffer ~8 authored inputs + a 5-field state
  ring; on a late input, restore + re-step). **Skip** spatial-index correctness and anti-cheat clamping — single
  player, those don't matter for a feel test.
- Drive it with `NetLatencySimulator.SetLatencyMs(50→75)` and read the **existing** `ReconcileOutcome` counters
  (`Matched`/`Corrected`/`Snapped`) + the `ServerMovementTrace` CSV. **Success = the `Snapped`/`Corrected` rate
  at 100–150 ms drops to near its 0 ms baseline.**
- ~3 files, ships nothing. Validates *feel*; deliberately ignores the two things that make the real migration
  hard (spatial-index rollback, anti-cheat), because those are safety, not feel.

## Bottom line
- This is the **correct** fix for the latency snap/hold — it removes the root cause (input-timeline mismatch),
  and it's the canon in our own catalogue.
- It fits us unusually well: the deterministic, parity-tested simulation and the shared tick clock **already
  exist**, so it's "change when inputs apply," not "rebuild movement" — smaller than the continuous-movement
  migration, and it likely **deletes** the commit-step + A/B duality + skew machinery on the way.
- It's still **L–XL** with real risks (spatial-index rollback, anti-cheat, CPU at scale, reversing the
  held-intent decision) — so **prove it with the cheap Phase-0 spike first**, measured on the `ReconcileOutcome`
  counters at 100–150 ms, before committing. Tile-stepped stays frozen and tagged throughout.
