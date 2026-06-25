# Phase 5 — Remote-Entity Interpolation (implementation spec)

Part of the continuous migration. Base: Phases 0–4 (continuous wire v37; local player predicts smoothly; REMOTE
entities render RAW/jumpy; monsters tile-step on the server [Velocity=0]; hop/TileInterpolator unwired). Phase 5 =
remote entities render SMOOTHLY. **Last phase before the drivable build.**

## Decision: position-sample INTERPOLATION now; extrapolation deferred (hybrid-ready)
- **Interpolation** (a fixed-delay playout buffer rendering slightly behind the newest received position) smooths BOTH
  continuous remote players AND **tile-stepped monsters** (glides between received positions — the continuous analog
  of the hop), with **NO wire change**.
- **Extrapolation** (`RemoteContinuousEntity` velocity dead-reckoning) does NOTHING for a Velocity=0 tile-stepped
  monster (the dominant remote case until Phase 8) and needs a per-entity velocity wire-add (the bandwidth Phase 3
  deferred). → **Defer it.** KEEP `RemoteContinuousEntity.cs` + tests in-tree; shape the seam so a later
  velocity-on-wire phase (alongside Phase 8) flips on per-entity extrapolation as a hybrid. NOT built now.

## RemotePositionInterpolator (new, `src/Mmo.Client.Core/Continuous/`)
Pure, Godot-free, headless-testable — the continuous `TileInterpolator`:
- Ring buffer of `(WorldVector pos, TimeSpan receivedAt)`; `Confirm(pos, receivedAt)` (was `TileInterpolator.Confirm(tile,…)`).
- `Sample(now)` renders at `playoutTime = now - InterpolationDelay`, **lerping continuously** (`RenderPosition.Lerp`)
  between the two bracketing samples (float positions, no cadence quantization).
- Delay: reuse `ResolveInterpolationDelay(cadence, isLocal:false)` + the live F1 "Remote interp buffer" knob +
  `RemoteInterpolationMinBufferMs` floor (unchanged).
- **Starvation → HOLD** at the newest sample (NO extrapolation — that's the deferred path).
- Port the `CatchUpQueueCap` runaway guard (time-domain): buffer backed up >~2 intervals → drop stale + fast-forward
  with a short final glide (no hard teleport).
- `Reset(WorldVector)` clears + snaps (respawn/AOI re-entry/teleport).
- **One driver for ALL remote kinds** (players + monsters glide the same; the hop arc is gone). Static resource nodes
  can hold one sample.

## Lifecycle + render seam
- `ClientEntity`: replace `_interpolator` (TileInterpolator) + `_hop` (MonsterHopInterpolator) with one `_remoteInterp`.
  `UpsertEntity` constructs it anchored on the spawn position; the placeholder→Monster reveal no longer swaps drivers
  (delete the `_hop` lazy-attach). `ApplySnapshot` feeds `_remoteInterp.Confirm(position, receivedAt)` for non-local.
  Despawn/AOI-exit: dies with the entity; AOI re-entry → fresh, anchored on re-entry pos.
- **Render seam** (`ClientEntity.ToRenderState`): local → `localOverride` (predictor, Phase 4, unchanged); **remote →
  `_remoteInterp.Sample(now)`** (was raw `FromWorld(Position)`); authoritative `Tile` in the render state unchanged.
  `now` is already threaded (currently discarded) — make it live. No per-frame Advance call needed (Sample is
  pure-functional on buffer+clock); the Godot loop is unchanged.
- **Authoritative `Position`/`Tile` unchanged** → targeting/harvest (`LocalTile`, `HarvestTargeting`) read confirmed
  tile, untouched (S53 holds for remote exactly as for local).

## Delete (retire the tile remote rendering)
`src/Mmo.Client.Core/TileInterpolator.cs` + `MonsterHopInterpolator.cs` (full files) + their tests; the `MmoClient`
hop API (`_hop`/`_hopDurationMs`/`SetHopDurationMs`/`MonsterHopDurationMs`/`SetMonsterHopDurationMs`), `CreateInterpolator`,
and the `EntityConfirmationDebug` hop/queue fields. **Keep** `ResolveInterpolationDelay`/`ResolveCadence`/
`SetRemoteInterpolationBufferMs` (repoint at `RemotePositionInterpolator`). **Godot/F1:** remove the "Monster hop
duration" knob (audit the Godot project for `MonsterHopDurationMs` reads first); keep "Remote interp buffer".

## Wire change: NONE (v37 stays). EntityStateSnapshot stays Position-only.

## Tests (`tests/Mmo.Client.Core.Tests/RemotePositionInterpolatorTests.cs`)
Smooth playout (continuous source: glides ~1 buffer behind, monotonic, no pops); **smooth playout (tile-stepped/monster
source: render strictly between the two tiles — the hop is gone, the slime glides)**; drop/jitter → HOLD at newest, no
fling; out-of-order ignored; catch-up cap collapses a backed-up buffer with a final glide; lifecycle (first sample
adopted, Reset snaps, respawn fresh); live buffer-knob re-times without discontinuity. Delete the
hop/TileInterpolator tests; migrate `MmoClient` tests that asserted `QueueDepth`/`IsHopping`. **Keep**
`RemoteContinuousEntityTests` green (the deferred-hybrid extrapolator).

## Sub-commits (order matters — never delete before the swap lands)
1. `feat(client): RemotePositionInterpolator + tests` (pure, unwired).
2. `feat(client): wire remote render to interpolation` (swap `_interpolator`/`_hop` → `_remoteInterp`; `ToRenderState`
   remote → `Sample(now)`; `Confirm` feeds WorldVector).
3. `refactor(client): delete TileInterpolator + MonsterHopInterpolator + hop API/wiring/tests`.
4. `chore(godot): remove the monster-hop-duration F1 knob; keep the remote-interp-buffer knob`.
5. `docs: Phase 5 progress`.

## Risks
- **Hop removal → monster render-behind-authoritative offset (the #1 feel watch).** The hop drew the monster ON its
  authoritative tile so melee landed where drawn; interpolation re-introduces a small render-behind offset.
  **Mitigation: PURELY COSMETIC — the server hit-check reads authoritative `Position`/`Tile`, NOT the render (S53), so
  hits are unaffected.** Keep the remote buffer small (the min-buffer floor) so the visual offset is sub-tile. Watch in
  feel-testing (can you still hit a gliding slime — yes, the server says so).
- Buffer-delay tuning (too large = laggy others, too small = jitter): the live F1 knob de-risks; default ≈ one snapshot
  interval (~50ms) + jitter margin.
- Tile-stepped glide may read "floaty" vs the hop's snappy arc — acceptable for a drivable build; tune in feel.
- No extrapolation → remote players trail by latency — expected; the deferred velocity phase closes it.
- Determinism NOT required (remote interp is cosmetic) — fuzzy float tolerance fine.

## What the human looks for (the drivable build)
Other players glide smoothly under F5 injected latency (trail slightly, not rubber-band); monsters no longer pop
tile-to-tile (glide, not too floaty); **melee on a moving monster still lands** (server checks authoritative — the
deliberate hop-vs-interp trade); AOI churn snaps cleanly (no long cross-screen glide on re-entry); local player still
tight (Phase 4 unchanged); the "Remote interp buffer" F1 knob trades smoothness vs responsiveness live; the
"Monster hop duration" knob is gone.
