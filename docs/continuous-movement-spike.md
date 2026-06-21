# Research Spike: Tile-Stepped → Continuous Movement

Status: **PARKED — a "maybe" as of 2026-06-21.** Decision: keep iterating on tile-stepped (grid) movement;
do NOT build the spike for now. Revisit only if varied/continuous speed (Albion-style mounts) becomes a
priority — the spike prototype below is the de-risking entry point. Research/decision-support only — no
production code was or will be touched by this. The tile-stepped game is frozen and tagged
`tile-stepped-stable` (commit `0d51037`). This doc pairs (a) how continuous movement is built, drawn from our
own `docs/networking-reference-catalogue.md` (canonical, already project-verdicted — not web research), with
(b) a grounded lift estimate for migrating *this* codebase.

## Why this came up

We want **varied, non-bracketed speed** (Albion-style mounts each a few percent apart). Our tile-stepped model
quantizes speed by construction: a step lands on a tick, so speed is forced into "1 tile per N ticks" — coarse
at the fast end (at 20 Hz there's a hole between 1.5× and 3× of walk). **Continuous movement removes that
quantization** — speed becomes a float velocity (units/sec), so any per-entity speed is trivial. That is the
defining reason to consider the migration; it is not a tuning tweak.

## How continuous movement is built (from our reference catalogue)

The canonical model is Gabriel Gambetta's four layers (catalogue Tier 1, "Client/server architecture canon"),
which our catalogue already scopes to this project:

1. **Authoritative server, fixed-timestep integration.** Each tick (Δt = 1/TickRate), for each moving entity
   `position += velocity·Δt`, `velocity = heading.Normalized · speedStat`. Speed is a stat; mounts/buffs set it
   directly. (Anti-speedhack stays free: the server owns the speed stat, so a client still can't move itself
   faster — the same guarantee the held-intent model prizes, without the commit-step machinery.)
2. **Client prediction** — apply local input immediately, tag each input with a **per-input sequence number**,
   send `(seq, heading, dt)`. (Catalogue verdict: prediction ADOPT-LATER, local-player only.)
3. **Reconciliation** — the server stamps the last-processed input seq on each snapshot; the client discards
   acked inputs and **replays the un-acked ones** from the authoritative position, blending/snapping the
   correction. This is *exactly* the re-anchor + re-project-in-flight philosophy our tile predictor already
   uses (S83) — re-implemented over input frames + float integration instead of tile steps.
4. **Entity interpolation** for remote players — render in the past, lerp between the two snapshots straddling
   render time, ~150 ms / 3-snapshot buffer at 20 Hz (Gaffer "Snapshot Interpolation"; catalogue verdict ADOPT
   NOW). Our `TileInterpolator` already embodies the buffered-playout idea; the sample type changes from
   "confirmed tile" to "float position + timestamp."

Supporting canon from the catalogue:
- **Rate decoupling (Valve Source):** simulation tick rate, snapshot send rate, and command rate are
  independent. So we can simulate at a higher rate for a fine speed dial **without** raising snapshot
  bandwidth — keep snapshots at 20–30 Hz.
- **Albion (David Salz talk):** memory-authoritative state + async write-behind persistence, shared
  client/server contracts, a three-layer client (input / sim / visualization). Confirms the
  continuous-MMO shape we'd be moving toward. (Catalogue notes the talk is secondary-sourced.)

## What migrating *our* code costs (lift estimate)

Full detail is in the architect pass; summary here.

**Survives largely intact (most of the system):** the snapshot/delta/AOI framework (per-client seq, acked
baseline, keep-alive, chunking), the spatial index design (`SpatialEntityGrid` — retype int-tile keys/distances
to float), the precise fixed-timestep tick loop, the F5/F6 live-toggle harness, **the render path** (it already
consumes a float `RenderPosition`; tiles only enter via `RenderPosition.FromTile`, so the renderer barely
changes), the held-intent input transport shape, the reconcile *philosophy*, and write-behind persistence.

**Throwaway:** the tile-step gate (`_nextEligibleTick`/`TryStep`/`StepHeldMovementIntents`), tick-quantized
cadence (`EffectiveStepCooldownTicks`, `MovementCadence`), Direction8 *as the movement unit* (survives as a
facing/anim enum), the entire commit-step system (`TryCommitStep`/`StepCommitRequest`/`CommitAcceptFraction`),
the S75 corner-cut rule (superseded by swept collision), and `LocalPlayerPredictor`/`LocalPlayerCosmetic` as
classes (replaced by one continuous predictor+reconciler).

**Phases & size** (S<1d · M~days · L~1–2wk · XL multi-week):

| Phase | Work | Size |
|---|---|---|
| 0 | Throwaway spike prototype (prove feel) | M |
| 1 | Server continuous position + speed stat + fixed-timestep integrator | M |
| 2 | **Continuous collision** (swept circle vs blocked-cell solids + wall-slide + anti-tunneling) | **L–XL** |
| 3 | Wire: float/fixed-point positions, continuous `MoveIntent` + per-input seq, drop commit-step, protocol bump | M |
| 4 | **Client continuous prediction + reconcile** (input ring, replay, blend/snap) | **L** |
| 5 | Remote interpolation → float position-sample playout buffer | M |
| 6 | AOI float retype (cell keys + distance) | S–M |
| 7 | Interaction/targeting: adjacency → interaction radius (float) | M |
| 8 | Persistence: float position columns + migration | S |
| 9 | Spawn/resource scatter in continuous space | S |
| 10 | Stress re-baseline + **bandwidth study** (float positions vs the per-client ceiling) | M |

**Total: solidly XL** — a multi-week milestone, not a task. The cost is dominated by **collision feel (2)** and
**prediction feel at latency (4)**, not by mechanical retyping.

### Highest risks
1. **Continuous collision feel** — wall-slide, no tunneling at mount speed, not snagging on cell corners (the
   continuous analog of S75). Where the "Albion feel" is won or lost.
2. **Prediction/reconcile feel** — we spent the whole S53→S103 arc getting *tile* prediction to feel right;
   continuous reopens that surface, and **sub-tile rubber-banding is more visible than tile snapping**. The
   philosophy transfers; the tuning does not. Budget a comparable iteration arc.
3. **Bandwidth** — float positions inflate the hot snapshot record; per-client bandwidth at 120–150 visible is
   a named limiter. Fixed-point + delta-vs-baseline is likely required, not optional. Must be *measured*.
4. **Float determinism** — tile prediction got exact integer parity; float parity is "within tolerance," which
   is fuzzier, so the reconcile error budget becomes a tuning parameter.

### Parallel mode vs hard replacement
A runtime A/B against tiles is **impractical** — position type, wire format, collision, and persistence all
change incompatibly; you can't mix continuous and tile entities in one snapshot, and our existing
`MovementRenderMode` A/B is a *client-render* toggle over one *server* model, while this changes the server
model. **Recommendation: throwaway spike branch to prove feel → then a hard replacement on its own
protocol-major branch, with tile-stepped `main` kept frozen** (tag `tile-stepped-stable`). Because the shared
frameworks carry over, the branch is a movement-core swap with wide-but-shallow seam edits, not a from-scratch
rewrite.

## Recommended next step: the minimal spike prototype (throwaway, on a branch)

Smallest thing that answers "does continuous feel right and reconcile cleanly?":
- **Server:** `WorldVector Position` + a fixed speed; per-tick `position += heading.Normalized·speed·Δt`; **swept
  circle vs `TileGrid._blockedTiles` solids with wall-slide** (collision MUST be in the spike — it's the unknown).
- **Wire:** hack float X/Y into the snapshot (don't optimize encoding) + continuous-heading `MoveIntent` with a
  per-input seq.
- **Client:** a continuous predictor (local integrate + replay un-acked from authoritative + blend/snap), feeding
  the existing float `RenderPosition` straight into the unchanged renderer.
- **Remotes:** simplest float position-sample lerp with a fixed playout delay.
- **Test** at injected latency (`NetLatencySimulator` already exists) and against walls. Wall-slide + reconcile
  feeling right at 100–150 ms de-risks the whole migration; if not, we learned it cheaply.

Deliberately skipped by the spike (known-shaped follow-ons, not feel risks): bandwidth-at-density, persistence,
AOI float retype, interaction radius, mount-speed tunneling edge cases.

## Bottom line
- Varied speed is *natively* a continuous-movement property; tiles will always fight it.
- Most of our system (AOI, snapshot/delta, persistence, tick loop, render, F-panels) survives — the migration is
  a movement-core swap, but an **XL** one, with the real cost in **collision** and **prediction feel**.
- Lowest-risk path: a **throwaway spike branch** that proves swept collision + continuous reconcile at latency,
  *before* committing to the full migration. Tile-stepped stays frozen and tagged the entire time.
