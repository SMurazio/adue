# S89 — Model B "Cosmetic lead": a live F5-toggleable local-movement render mode (A/B against prediction)

Severity: S (movement feel — the user wants to A/B the prediction model live without a rebuild). Client-only;
no server/protocol change. Default behavior UNCHANGED (model A stays the default; B is opt-in via F5).

## Why

We have ONE local-movement model today — **model A, "full tile prediction"** (`LocalPlayerPredictor` +
`Reconcile`): the client owns a `PredictedTile` AHEAD of the server's confirm, then reconciles/rubber-bands it
back. The user dislikes the reconcile loop itself (not latency in general) and wants to feel an alternative
back-to-back. This task adds **model B, "cosmetic lead"**, as a second render driver you can flip on/off live
from the F5 panel, so A and B can be compared in the same session.

**Three models, for shared vocabulary (do not blur them):**
- **A — full tile prediction (what we have).** Client owns `PredictedTile` before the server confirms.
  Reconcile + rubber-band. Logic (harvest) reads the confirmed `LocalTile`. NOT cosmetic.
- **B — cosmetic lead (THIS task).** Only the confirmed tile is truth; it advances ONLY on a server ack
  (snapshot). The avatar's *pixels* may animate/glide toward the held-input direction early, but **no tile is
  ever banked ahead for logic**. A disagreeing confirm CUTS/snaps the render to the confirmed tile — there is
  no step-seq reproject, no reconcile loop. "No positional prediction," not "no prediction." This is
  UO-per-step-approve in spirit: the server gates each tile; the client may animate early.
- **C — full server follow (NOT this task, explicitly rejected).** Local player treated like a remote:
  confirmed tiles only, buffered interpolator, playout delay → laggy. Do NOT build C and do NOT call B "follow
  the server."

By construction B **cannot** produce the at-rest latch or the spam desync: there is no `PredictedTile`, so the
F5 green (predicted) marker can never diverge from magenta (server) — there is no green tile at all in B.

## The decided model-B behavior (architecture — implement as specified)

Add a new local-player render driver (e.g. `src/Mmo.Client.Core/LocalPlayerCosmetic.cs`,
`LocalPlayerCosmetic`) that runs PARALLEL to `LocalPlayerPredictor` and is selected by a runtime mode flag.
It owns the SAME present-time render tween machinery the predictor uses (`RenderPosition` from/to over one
cadence; reuse `RenderPosition.FromTile` / `RenderPosition.Lerp`), but with NO predicted tile and NO
step-sequence:

1. **Confirmed tile is the only state.** It advances ONLY in `Confirm(tile, facing, now)` (called from
   `EntityState.ApplySnapshot`, the server ack). There is no `PredictedTile`, no `PredictedStepSeq`, no
   `Reconcile`/replay/cap.
2. **Render glide between confirmed tiles.** On `Confirm`, retarget the tween from the CURRENT render position
   toward the new confirmed-tile center over one cadence, so consecutive confirmed steps glide continuously
   (identical to how one server step looks today).
3. **Early-start cosmetic lead (the snappy part).** The instant `SetIntent(moving=true, dir, now)` arrives AND
   the render is settled on the confirmed tile, begin gliding from the confirmed tile toward the ADJACENT tile
   in `dir`, up to a bounded lead of `CosmeticLeadTiles = 1.0` tile ahead of the confirmed tile. This is the
   responsiveness: motion appears immediately, before any server confirm. The lead is RENDER-ONLY — it never
   advances the confirmed tile and is never read by logic.
   - **Walkability-gated (cosmetic gate, still model B):** only start/continue the glide toward a tile the
     same oracle the predictor uses (`MmoClient.IsWalkableForPrediction`, with the S75 diagonal corner-cut
     rule) says is walkable. This is a cosmetic gate on the glide DIRECTION — no tile is banked — so it stays
     pure B while avoiding ugly glide-into-wall-then-snap on every wall press. Pass the oracle in the same way
     `AttachPredictor` does.
   - **Lead cap behavior at high latency:** if the glide reaches the `CosmeticLeadTiles` cap before a confirm
     advances the confirmed tile, HOLD at the cap (soft wait) until the next confirm. This is the honest
     "paced by confirm rate" trade; on LAN confirms arrive ~every tick so the hold is invisible.
4. **Confirm reconciliation = cut/snap, never reproject.**
   - If the new confirmed tile is the tile the lead was gliding toward (server agreed): continue seamlessly
     (retarget per #2 — the glide flows into the confirmed step).
   - If the confirmed tile is NOT where the lead was heading (blocked / different): CUT the render to the
     confirmed tile (snap, or a short ≤1-cadence blend — implementer's call on blend-vs-hard-cut for feel).
     No step-seq math, no in-flight reproject. This is the only correction in B.
5. **Facing is cosmetic.** `Facing` returns the held direction while moving (rotate immediately on input),
   else the confirmed facing. (Same spirit as the predictor's live facing.)
6. **At rest.** On `SetIntent(moving=false)`, stop extending the lead; the glide settles onto the confirmed
   tile (confirmed IS truth, so it converges exactly — no latch possible).
7. **`CalibrateToServerTick` / tick-grid: not needed in B.** B does not run the server tick gate; it glides on
   wall-clock cadence and is corrected by confirms. Provide a no-op so the call site stays uniform.

## Live A/B toggle (F5 — NOT a launch flag; binds the live-toggle guardrail)

- Add a movement render mode to `MmoClient` — e.g. `enum MovementRenderMode { Predicted, CosmeticLead }` with
  `public MovementRenderMode RenderMode { get; set; }` (default `Predicted` — A stays the shipped default).
  Mirror the existing `PredictionEnabled` property pattern (`MmoClient.cs:194-198`).
- Route the local-player driver by the mode at the four touch points: `SendMoveIntent`/`SetIntent`
  (`:235-247`), the per-Poll `Tick` (`:189`), `EntityState.ApplySnapshot` (`:847-889`), and the render-source
  selection in `ToRenderState` (`:938-947`). In `Predicted` mode everything behaves EXACTLY as today; in
  `CosmeticLead` mode the local entity is driven by `LocalPlayerCosmetic` instead. Keep `Tile` = confirmed in
  BOTH (already true at `:853`) so harvest/targeting is unaffected.
- **Re-anchor on a live switch (no jump):** when `RenderMode` flips mid-session, seed the newly-active driver
  from the local entity's current confirmed `Tile` + current render position so the avatar doesn't pop.
- **F5 checkbox "Cosmetic lead (model B)"** in `MmoClientRoot` — copy the "Prediction tiles" checkbox +
  handler pattern (`MmoClientRoot.cs:757-761` and `ApplyPredictionTiles` `:812-818`); the handler calls
  `_client.SetMovementRenderMode(enabled ? CosmeticLead : Predicted)` (or sets `RenderMode`). Admin-gated like
  the rest of F5. Flipping it must take effect WHILE THE CLIENT RUNS — no restart, no rebuild.
- The exact internal shape (a parallel `_cosmetic` field + mode branch, OR a shared
  `ILocalPlayerRenderer` interface that both `LocalPlayerPredictor` and `LocalPlayerCosmetic` implement and the
  mode swaps) is the implementer's call — as long as **A stays the default and is behaviorally untouched**,
  and reverting THIS commit cleanly removes B and leaves A. Do not rename/alter `LocalPlayerPredictor`'s
  existing public methods.

## Tests (the gate — fail-before is N/A since B is new; assert B's invariants)

New `tests/Mmo.Client.Core.Tests/LocalPlayerCosmeticTests.cs`:
- **Logic never leads:** after any sequence of `SetIntent` + `Tick` with NO confirm, the confirmed tile is
  unchanged (B banks nothing); it advances only on `Confirm`.
- **Early glide:** `SetIntent(moving, dir)` then `Sample` a few ms later (before any confirm) shows the render
  position moved off the confirmed-tile center toward `dir` (responsiveness), bounded by `CosmeticLeadTiles`.
- **Blocked confirm cuts, no reproject:** glide toward a tile, then `Confirm` a DIFFERENT (blocked) tile →
  render ends at the confirmed tile within ≤1 cadence; assert no overshoot persists and the confirmed tile is
  authoritative (the exact symptom A latches on — B must not).
- **At rest == confirmed exactly:** after `SetIntent(moving=false)` + a steady confirmed tile, the render
  settles onto the confirmed tile center (no residual lead, no latch).
- **Walkability gate:** with a blocked adjacent tile, the early glide does NOT start into it.
- Keep ALL existing suites green — especially the full `LocalPlayerPredictorTests` (A is untouched) and
  `MmoClientDeltadOutReconcileTests`.

## Constraints

- Client-core + Godot client only. No server/protocol/wire change. `Tile`/`LocalTile` stays confirmed-only.
- `.\.shared\skills\mmo-dev\scripts\run-checks.cmd` green before AND after. You cannot run Godot; the
  Orchestrator runs the live A/B (flip F5, spam down/left in each mode, confirm B never latches and the green
  marker is absent in B).
- **Safe Local Execution** binds you (scripts only; no ad-hoc launchers/PID-kills). Diagnostics/toggles are
  LIVE in-client (F5) — no env-var/launch gating, no restart to switch modes.
- One discrete, revertable commit referencing this filename; delete this file in that same commit on success.
  If the scope feels too large for one commit or an architectural fork appears (e.g. interface vs parallel
  field has a real downside), STOP and surface it rather than guessing.
- Add a short "Model B (cosmetic lead)" note to `docs/movement-input-model.md` describing the mode + the A/B/C
  vocabulary, in the same commit.

## Acceptance

- F5 "Cosmetic lead (model B)" flips the local player between A (prediction, default) and B (cosmetic lead)
  LIVE, no restart, no avatar pop on switch.
- In B: confirmed tile advances only on snapshot; the render glides early on input (snappy) and is bounded by
  `CosmeticLeadTiles`; a disagreeing confirm cuts to the confirmed tile with no reproject; at rest the render
  is exactly on the confirmed tile; harvest/targeting (on `LocalTile`) unaffected.
- A is the default and behaviorally identical to today. `run-checks` green incl. all A tests + new B tests.
- Review-request → `review/review-request-s89-cosmetic-lead-mode.md`. Do NOT push.
