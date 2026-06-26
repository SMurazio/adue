# N — live `_renderDivergence` diagnostic compares against the ROUNDED TILE, not the continuous pos

## Problem
`MmoClientRoot.SampleMotionMetrics` (`src/Mmo.Client.Godot/MmoClientRoot.cs:~1768-1786`) computes the local
player's render-vs-confirmed divergence by comparing the continuous `RenderX/RenderY` against
`_client.LocalTile` — the **rounded confirmed TILE** (`TileCoord`, integer-valued; `_confirmedX/_confirmedY` are
`int`). The continuous authoritative position can sit up to ~0.5 tile/axis (~0.7 tile diagonal) from its rounded
tile, so this diagnostic adds up to ~0.7 tile of pure ROUNDING error to the reported divergence even when
prediction is perfect.

This is diagnostics-only (it does NOT affect movement), but it inflates the headline "divergence" readout +
the frame-log CSV `divergence` column + the telemetry, which muddies prediction measurement. It surfaced while
diagnosing the prediction-regression rubberband: the live "~0.7u baseline divergence" was largely this artifact,
not a real predicted-vs-authoritative gap.

## Fix
Compare `RenderX/RenderY` against the **continuous confirmed position** (the same `Position`/predictor
`ServerX/ServerY` the reconcile uses), not the rounded `LocalTile`. Surface the continuous confirmed pos to the
metrics sampler (e.g. read the local `ClientEntity.Position` or the predictor's `ServerX/ServerY`) and use it for
`_renderDivergence`. Keep `LocalTile` for anything that genuinely needs the tile (targeting/aim), but the
movement-quality divergence must be continuous-vs-continuous.

## Acceptance
- `_renderDivergence` (F3 HUD + CSV `divergence` column + telemetry) reads ~0 during clean steady-state
  prediction instead of a ~0.5–0.7 tile floor.
- No movement/behaviour change (diagnostics-only edit).
- Trivial-to-standard risk; gate the client build + a quick visual sanity check of the readout.
