# S92 — Make model B the default; add an "accept/deny only" mode (no cosmetic pre-movement)

Severity: S (movement feel — user direction). Client-only; no protocol/server change. Builds on S89/S91.

## Why

Model B (cosmetic lead) is now the chosen baseline. But B's early cosmetic lead means the render glides ahead
of the confirmed tile and must SNAP back on release (S91) — and the camera hard-follows the avatar
(`MmoClientRoot.UpdateCamera` `:1070`, `_camera.Position = focus + offset`), so that snap pops the camera. The
user's hypothesis: the pop is caused by the snap / cosmetic pre-movement, not the camera follow. So instead of
smoothing the camera, **try removing the cosmetic pre-movement**: an "accept/deny only" mode where the avatar
moves ONLY when the server confirms a step (accept = confirmed tile advances → tween to it; deny = confirmed
tile unchanged → stay), with NO early lead and NO release snap. No lead ⇒ no overshoot ⇒ no snap ⇒ no camera
discontinuity to pop on.

This is the SAME `LocalPlayerCosmetic` driver with the forward lead switched off — a small change.

## Two parts

### 1. Make model B the default
`src/Mmo.Client.Core/MmoClient.cs`: change `_renderMode` default from `MovementRenderMode.Predicted` to
`MovementRenderMode.CosmeticLead` (`:69` area). Model A (`Predicted`) stays in the enum and code (still reachable
programmatically) but is no longer the default and is dropped from the F5 toggle (see part 3). Keep model A's
code path intact (the predictor is still attached dormantly by `EnsurePredictor`).

**Watch:** any existing test that assumed the local player is predicted-by-default (drives via
`LocalPlayerPredictor`) may change behavior now that B is default. Set the mode explicitly to `Predicted` in
those tests (or update their expectations) — keep the whole suite green, no silent breakage. The standalone
`LocalPlayerPredictorTests` test the predictor directly and are unaffected.

### 2. Add the "accept/deny only" mode
- Add `MovementRenderMode.AcceptDeny` to the enum (`MmoClient.cs` near `:7`). Both `CosmeticLead` and `AcceptDeny`
  are driven by the cosmetic driver; only `Predicted` uses the predictor. So change the "cosmetic drives the
  render" predicate at every routed touch point from `== CosmeticLead` to `!= Predicted` (the per-Poll `Tick`
  routing `:196-216`, `SendMoveIntent` routing `:295-309`, `ClientEntity.ApplySnapshot` `:933-947`,
  `ClientEntity.ToRenderState` `:1085-1108`, and the `_cosmeticActive` gating).
- `src/Mmo.Client.Core/LocalPlayerCosmetic.cs`: add a settable `bool LeadEnabled` (default `true`). When
  **false** (accept/deny):
  - `Tick`: do NOT arm/extend the forward lead (skip the glide-arming block at `:148-178`); just sample the tween
    forward. `_leadTarget` stays null. The render moves ONLY via `Confirm`.
  - `SetIntent` release branch (`:130-138`, the S91 snap): do NOT snap. Just `_moving = false` and leave the
    current tween to finish — there is no lead overshoot to unwind, and the in-progress tween is always toward a
    confirmed (truth) tile, so letting it complete is correct and avoids any release discontinuity.
  - `Confirm`: UNCHANGED — it tweens from the current render to the new confirmed tile over one cadence (the
    accepted-step movement) or stays put when the tile is unchanged (deny). This is the only thing that moves the
    avatar in accept/deny.
  - Keep `LeadEnabled == true` = exactly the current S89/S91 model B (lead + snap-on-release), unchanged.
- Wire `LeadEnabled` from the mode: in `ClientEntity.ReanchorLocalDriver` (and the activation in
  `EnsurePredictor`), set `_cosmetic.LeadEnabled = (mode == MovementRenderMode.CosmeticLead)` and `_cosmeticActive
  = (mode != Predicted)`, then re-anchor as today so a live switch doesn't pop.

### 3. F5 toggle: B (default) ↔ accept/deny
`src/Mmo.Client.Godot/MmoClientRoot.cs`: repurpose the existing S89 checkbox (`_cosmeticLeadCheck`,
`ApplyCosmeticLead` `:846-852`, built at `:766-772`). New label e.g. **"Accept/deny only (no lead)"**, default
**unchecked** = model B (`CosmeticLead`, the new default); **checked** = `AcceptDeny`. Handler:
`_client?.SetMovementRenderMode(enabled ? MovementRenderMode.AcceptDeny : MovementRenderMode.CosmeticLead)`.
Admin-gated, live, no restart (as today). (Model A is no longer on the F5 toggle; that's intended.)

## Tests

New/updated in `tests/Mmo.Client.Core.Tests/LocalPlayerCosmeticTests.cs` (drive `LeadEnabled = false`):
- **No pre-movement:** `SetIntent(moving) + Tick` with NO `Confirm` leaves the render EXACTLY on the confirmed
  tile (it never glides ahead) — contrast the lead-enabled `SetIntent_GlidesRenderEarly` test.
- **Moves only on accept:** after `Confirm` advances the tile, the render tweens tile-to-tile and reaches the new
  confirmed center over one cadence (smooth, not a teleport).
- **Deny = no move:** an unchanged-tile `Confirm` leaves the render where it is.
- **Release does not snap:** glide is impossible (no lead), and releasing mid-`Confirm`-tween does NOT jump the
  render — it continues to the confirmed tile (assert no instantaneous position jump at the release instant).
- Keep ALL S89/S91 lead-enabled invariants green (B still leads + snaps on release).
- Keep `LocalPlayerPredictorTests` + the rest of the suite green; fix any test that assumed A-as-default.

## Constraints

- Client-core + Godot client only. No protocol/server/wire change. `Tile`/`LocalTile` stays confirmed-only.
  Model A's code path stays intact (just not the default / not on the F5 toggle). No camera code changes in this
  task (the whole point is to test whether removing the snap fixes the camera WITHOUT touching the camera).
- `.\.shared\skills\mmo-dev\scripts\run-checks.cmd` green before/after. You cannot run Godot — the Orchestrator
  runs the live check (toggle accept/deny; walk + release; confirm no camera pop and no avatar snap).
- **Safe Local Execution** binds you. Do NOT commit, push, or delete the task file — leave the tree dirty + write
  `review/review-request-s92-accept-deny-mode.md`; the Orchestrator verifies and commits. (Same loop as S89/S91.)

## Acceptance

- Default render mode is model B (`CosmeticLead`). F5 "Accept/deny only (no lead)" flips B ↔ accept/deny LIVE.
- In accept/deny: the avatar never leads, moves only on a confirmed step (smooth tile-to-tile), a deny doesn't
  move it, and release does NOT snap — so a hard-following camera has no discontinuity to pop on.
- Model B unchanged when lead is enabled; model A intact (not default). New accept/deny tests + all S89/S91
  invariants + predictor tests green; run-checks green.
