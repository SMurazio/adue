# S67 — Movement motion-quality instrumentation (continuous position + divergence + per-frame motion)

Severity: tooling / observability. Today the debug readouts and the per-frame CSV are **tile-quantized /
timing-only**, so sub-tile movement quality — the actual glide between tiles, turn snappiness, reconcile
snaps — is NOT measurable; only frame pacing is. Add instrumentation so motion quality becomes quantifiable
(for Orchestrator/MCP analysis and for the human via F3). Diagnostics only — **do not change movement
behavior**.

## Compute (per frame, local player)
1. **Continuous render position** — the actual fractional world position the local avatar is *drawn* at
   (the predicted/tweened position, NOT the tile). Source it from the same place the renderer/camera uses —
   the local entity's render-state position / the local `PlayerVisual` transform — not `LocalTile`. (Today's
   readout reports the tile, which is why MCP `client_entities` render x/y look integer.)
2. **Confirmed position** — the latest server-confirmed tile (`MmoClient.LocalTile`).
3. **Render↔confirmed divergence** (tiles) — `|continuousRender − confirmed|`; track current, **session max**,
   and a **snap counter** (increment when divergence jumps > a small threshold between consecutive frames —
   a reconcile snap).
4. **Per-frame motion delta** — distance the continuous render position moved since the previous frame
   (≈ instantaneous speed; near-constant within a step = smooth glide, spikes/zeros = stutter).

## Surface in
a. **Per-frame CSV** (`.run/client-frames.csv`): add columns `localRenderX, localRenderY, confirmedX,
   confirmedY, divergence, frameDelta`. Primary offline-analysis channel — keep the existing columns/order,
   append the new ones.
b. **Debug control readout** (`DebugControlChannel.cs` → MCP `client_entities`): report the local player's
   render x/y as **continuous (fractional)**, not tile-rounded, so MCP reads real sub-tile position. If
   straightforward, add a motion summary (`maxDivergence, snapCount, currentSpeed`) to the `client_telemetry`
   payload too.
c. **F3 HUD**: one live line — continuous pos, current speed (tiles/s), max divergence, snap count — so the
   human sees motion quality live alongside the perf HUD.

## Constraints
- Client-only; no protocol/server change. **Instrumentation only — zero movement-behavior change.** Keep the
  per-frame hot path cheap (a few subtractions; the CSV writer already exists — extend it).
- Land as **separable commits** where natural: (i) compute + CSV + readout (the measurement data), (ii) the
  F3 HUD line. (Per the one-change-one-commit rule.)
- Run `.\.shared\skills\mmo-dev\scripts\run-checks.cmd` before/after. You can't run Godot — Orchestrator runs
  `godot-build` and verifies by driving (post-relaunch): the continuous position varies sub-tile during a
  step, divergence stays ≈0 in steady motion, the CSV columns populate, MCP `client_entities` shows
  fractional render. Human checks the F3 line. **Safe Local Execution** binds you.

## Acceptance
- `godot-build` green; CSV has the continuous-position + motion columns; MCP `client_entities` reports
  fractional render; F3 shows a motion line; movement behavior unchanged. Review-request(s) →
  `review/review-request-s67-motion-instrumentation.md`. Do NOT commit or delete the task file.
