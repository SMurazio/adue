# Plan: UO-style client-driven movement as a new render mode

> **Update (RENDER1, 2026-06-21):** shipped (UO1–UO5). The F6 cycle is now trimmed to **CosmeticLead +
> UoClientDriven** (Predicted/AcceptDeny removed) and UO is the default boot mode. UO loss/latency hardening
> continues in the netcode redesign milestone (`movement-netcode-redesign-plan.md`).

Status: **design, ready to implement** on `review/tile-step-todo` (production, NOT the spike branch). Default
render mode stays `CosmeticLead`; UO mode is a parallel, opt-in, fully revertable addition.

## What "UO mode" is
Ultima Online's proven model: **instant client prediction + the server FOLLOWS the client's per-step requests
(accept/reject)**, instead of the server pacing movement autonomously. The client moves on keypress (banks
tiles), and for *each* step sends a server-validated request; the server advances the entity only on accepted
requests; a reject snaps the client to server truth. (See the UO discussion + `networking-reference-catalogue.md`.)

## Why it's mostly assembly, not new math
Every hard mechanism already exists:
- **Instant prediction** — `LocalPlayerPredictor` already steps on keypress, banks tiles, bumps `PredictedStepSeq`,
  reconciles against `RecipientStepSeq`.
- **Per-step validated request** — S103 commit-step: `StepCommitRequestMessage` → `GameServer.HandleStepCommit`
  (GameServer.cs:1603) → `Zone.TryCommitStep` → `WorldEntity.TryCommitStep` (WorldEntity.cs:269) already does
  walkability + the anti-cheat floor (`CommitAcceptFraction=0.5`) + the no-speedhack borrow + `StepSequence++`.
  Today it fires ONCE on release; UO mode fires it for EVERY step.
- **Reject→snap** — the predictor's existing `RecipientStepSeq` reconcile snaps on divergence. No new client
  reconcile code (we do NOT need the cosmetic driver's `PendingCommit` grace machinery).
- **Anti-cheat for free** — the cooldown gate (`WorldEntity._nextEligibleTick`) + the borrow cap step rate
  regardless of how fast the client requests, so unlike real UO we need NO movement throttle / fastwalk detector.

## The only genuinely new surface
1. A 4th `MovementRenderMode.UoClientDriven = 3` routed through the **predictor** branch (a `UsesPredictor(mode)`
   helper at the 4 routing sites in `MmoClient.cs`).
2. **Per-step commit emission**: surface the accepted-step direction(s) out of `LocalPlayerPredictor.Tick`, and
   in `MmoClient.Poll` send one `StepCommitRequestMessage(++_moveSequence, dir)` (ReliableOrdered) per accepted
   step.
3. A **one-bit "this session is client-driven" signal** (`MovementModeMessage(bool ClientDriven)`) so the server
   **stops auto-pacing** that entity — a one-line guard in `StepHeldMovementIntents` skips client-driven sessions.
   This prevents DOUBLE-STEPPING (the held-intent pacer + the commits both stepping the entity). The client keeps
   sending `MoveIntent` for stop/keepalive/facing; the server just ignores it for *pacing* when the flag is set.

## Staged, revertable commits (one discrete commit each)
1. **Protocol (S):** `MessageType.MovementMode` + `MovementModeMessage(bool ClientDriven)` + codec + a
   `ProtocolCodecTests` round-trip. No behavior. Version bump (server+client ship together).
2. **Server honors the flag (S):** `ClientSession.ClientDrivenMovement` + handle `MovementModeMessage` +
   one-line `continue` in `StepHeldMovementIntents` for client-driven sessions. Server test: a flagged session
   with a held MoveIntent does NOT advance via the tick loop. Still inert (nothing sets the flag).
3. **Predictor surfaces accepted-step directions (M):** `LocalPlayerPredictor.Tick` reports the directions of
   steps accepted this call (caller-supplied buffer to handle multi-step catch-up at LocalPlayerPredictor.cs:317).
   Pure, unit-tested. No wiring yet.
4. **Client UO mode + per-step emission (M):** `MovementRenderMode.UoClientDriven=3`; `UsesPredictor` helper;
   route the 4 sites through the predictor; per accepted step send a `StepCommitRequest`; on entering/leaving the
   mode send `MovementModeMessage`. Default stays `CosmeticLead`. Client tests (MmoClientCommitStepTests style):
   N commits for N predicted steps; mode message on toggle.
5. **Make it selectable + docs (S):** add `UoClientDriven` to `RenderModeCycle` (MmoClientRoot.cs:943) so the F6
   render-mode button cycles to it; line in `docs/movement-input-model.md`.

## Risks / tuning (from the design pass)
1. **Anti-cheat floor at normal cadence (highest).** `CommitAcceptFraction=0.5` was tuned for release commits. A
   faithfully-paced client-driven step arrives at/after the nominal step end, so the floor should never reject
   it — but clock skew/jitter under latency could push a legit step below 0.5 elapsed → spurious reject →
   micro-snap. Ship the floor UNCHANGED; A/B under `SetSimulatedLatencyMs`; only if snap-backs appear add a
   separate `ClientDrivenAcceptFraction` (do NOT weaken the release floor). The borrow caps rate regardless, so
   no throttle is needed — confirmed.
2. **Double-stepping** if the `ClientDrivenMovement` flag fails to set (lost message, reconnect, AOI re-entry) →
   2× speed. Send `MovementModeMessage` ReliableOrdered; re-send on (re)login/respawn; add a cheap server trace
   if a non-flagged session receives commits.
3. **`MaxInFlightLead=2` cap** (LocalPlayerPredictor.cs:80) may clip a legit >2-step committed lead at high RTT
   (briefly under-predicts; never a correctness bug). Consider raising for client-driven; observe first.
4. **Reliable-ordered commit volume** (~7/s/session at 140ms cadence). Low, but note for the stress gate.

## Critical files
- `src/Mmo.Client.Core/MmoClient.cs` — enum, routing, per-step emission, mode-signal send.
- `src/Mmo.Client.Core/LocalPlayerPredictor.cs` — surface accepted-step directions from `Tick`.
- `src/Mmo.Server/Runtime/GameServer.cs` — handle `MovementModeMessage`; guard `StepHeldMovementIntents`; reuse `HandleStepCommit`.
- `src/Mmo.Server/Runtime/ClientSession.cs` — `ClientDrivenMovement` flag; shared `_moveSequence` cursor.
- `src/Mmo.Shared/Protocol/{Messages.cs,MessageType.cs,ProtocolCodec.cs}` — new `MovementModeMessage`.
- `src/Mmo.Client.Godot/MmoClientRoot.cs` — add to `RenderModeCycle` (selectable).
