# Phase 5 (remote interpolation) review follow-ups

From the Phase 5 independent review (commit `ef8286d`, verdict SHIP). Non-blocking.

## A — Reset-on-AOI-re-entry for delta-dropped entities (minor)
If a REAL (non-placeholder) entity silently falls out of *delta* snapshots WITHOUT an `EntityDespawn` and later
re-appears, `UpsertEntity` takes the `existing` branch and `Confirm`s the re-entry position onto the still-live
buffer → a one-time cross-screen GLIDE rather than a snap (because `RemotePositionInterpolator.Reset` is never
called from `MmoClient`). **This is PRE-EXISTING parity** — the old `TileInterpolator`/`MonsterHopInterpolator`
`Reset` was also never called from `MmoClient` — carried forward, now a continuous glide instead of a tile pop, not
a Phase-5 regression. Fix: call `_remoteInterp.Reset(position)` when a re-confirm jump exceeds a threshold (or on a
detected AOI re-entry). Low priority.

## B — deferred velocity-on-wire + extrapolation (the hybrid)
Phase 5 chose interpolation only; velocity-extrapolation was deferred. `RemoteContinuousEntity` (the extrapolator)
was assumed in-tree but only ever existed on the `exp/continuous-movement` branch — there was nothing to keep.
When a future phase adds per-entity velocity to the wire (alongside Phase 8, so monsters actually carry velocity),
**port `RemoteContinuousEntity` from `exp` + its tests** and make `RemotePositionInterpolator` a hybrid (interpolate
by default; extrapolate per-entity when replicated velocity is non-zero). Gate behind the Phase 12 bandwidth study.
