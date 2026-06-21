# Movement Input Model — held-direction intent (replaces MoveStep streaming)

Design + decision record. Decided 2026-06-19 (user-directed, from the "wrong-model" audit).

## The problem (why the current model is wrong for this game)

Movement is tile-stepped, server-authoritative, **no client prediction** (confirmed-state tweening —
see `networking-design-plan.md`). But the *input* model is FPS-prediction-shaped: while a direction key
is held, the client streams a `MoveStep(inputSeq, direction)` **every server tick** (~20/s), and the
freeze fix (commit 5a609be) deliberately raised the send rate to tick-rate so the server always has a
fresh step queued at its cooldown boundary. `N21` was queued to *tune that send rate down*.

That's optimizing a workaround. A stream of sequenced per-step inputs exists to support prediction +
reconciliation — which this project rejected. For a **no-prediction** server-authoritative tile-stepper,
the correct model is **input as state, not events**: the client tells the server *what it intends*
("holding W" / "stopped"), and the server steps the entity at its own cadence from that intent. The
redundant per-tick traffic disappears (the thing N21 was tuning), and the server — which now always
knows the held direction — paces steps perfectly evenly on its own, which is exactly what the freeze fix
was straining to achieve from the client side.

## The model

- **Client → server: `MoveIntent(seq, moving, direction)`** — sent on **change** (keydown, keyup,
  direction change) plus a **low-rate keepalive** (resend current intent every ~500 ms). `moving=false`
  = stopped (direction ignored). **Reliable-ordered** delivery (it's *state*; a dropped "stop" must not
  be lost). Volume: a few packets per second, vs ~20/s today.
- **Server state per session:** `{ moving, direction, lastSeq, lastIntentTick }`. Ignore intents with
  `seq <= lastSeq` (stale-input rejection; reliable-ordered already orders, the seq is belt-and-suspenders
  + anti-cheat).
- **Server tick:** for each session whose intent is `moving` and whose step cooldown has elapsed, attempt
  one tile step in `direction` with the **same validation as today** (bounds, walkability, cooldown). If
  the target tile is blocked, don't step (stay; keep the intent — it moves when unblocked or redirected).
  Stepping is unchanged tile-stepping; only its *trigger* moves from "a queued MoveStep" to "held intent".
- **Safety timeout:** if a `moving` session sends no intent (not even keepalive) for ~1 s, the server
  clears the intent to stopped. Guards a wedged-but-connected client from walking forever. (A real
  disconnect already despawns the entity, so this is an edge-case net.)

## Why this is safe / preserves guardrails

- **Still server-authoritative, still no prediction, still tile-stepped.** Only the input *representation*
  changes. The server validates every step exactly as now; the client cannot move faster than the
  cooldown (it never could — the server enforces it).
- **Anti-cheat:** the per-intent `seq` keeps stale-input rejection. No new surface — arguably less (no
  burst of step requests to validate).
- **Feel:** the server steps exactly at each cooldown boundary from the held intent → perfectly even
  cadence, no dependency on client send timing. This should match or beat today's feel and **must not
  reintroduce the freeze** — re-verify the server step-cadence (the `server-steps.csv` cluster check) and
  a human smoothness pass.

## Protocol

- Bump version (currently **v14**). Replace `MoveStep` with `MoveIntent` (client no longer sends discrete
  steps). Both clients are ours, so a clean replacement is fine. `WorldSnapshot`/tile semantics unchanged.

## Migration / removal

- Remove the client's per-tick `SendHeldMovement` MoveStep stream; send `MoveIntent` on change + keepalive.
- Server's `MoveStep` handler becomes the `MoveIntent` handler + the per-tick intent-stepping loop.
- Update both clients (Godot + web debug).

## Verification

- Headless: step-validation unit/integration tests (held intent steps at cooldown; blocked tile stops;
  stale seq ignored; keepalive-timeout clears intent; reliable "stop" halts movement).
- Stress: move/s inbound drops sharply vs today (the N21 goal, achieved by the model not a tuning knob);
  server step cadence stays even (no freeze).
- **Human feel-check (required):** smooth movement, responsive start/stop, no runaway on key release.

## Local-player render models — A / B / C vocabulary (S89)

The *input* model above (held intent → server steps) is shared by every client. How the LOCAL avatar's
*pixels* are driven on top of it is a separate, live-switchable choice. Three named models, kept distinct:

- **A — full tile prediction (the shipped default).** `LocalPlayerPredictor`. The client owns a
  `PredictedTile` AHEAD of the server's confirm, mirrors the server's tick-grid step loop, and
  reconciles/re-projects on every snapshot (re-anchor + capped in-flight replay). Logic (harvest/targeting)
  reads the confirmed `LocalTile`, but a predicted tile exists and the F5 green (predicted) marker can
  diverge from magenta (server) under lag/spam. NOT cosmetic.
- **B — cosmetic lead (S89, opt-in via the F5 "Cosmetic lead (model B)" toggle).** `LocalPlayerCosmetic`.
  The ONLY state is the confirmed tile, which advances ONLY on a server ack (`Confirm`, from
  `EntityState.ApplySnapshot`). The avatar's *pixels* may glide toward the held-input direction early — a
  bounded **cosmetic lead** of `CosmeticLeadTiles = 1.0` tile, walkability-gated on the glide direction
  (the same S75 corner-cut oracle model A uses, gating direction only) — but **no tile is ever banked
  ahead for logic**: there is no `PredictedTile`, no step-seq, no `Reconcile`/replay. An *agreeing* confirm
  flows seamlessly into the confirmed step; a *disagreeing* confirm CUTS the render to the confirmed tile
  (a ≤1-cadence blend, no reproject). "No positional prediction," not "no prediction" — UO-per-step-approve
  in spirit: the server gates each tile, the client animates early. By construction B cannot produce A's
  at-rest latch or the spam desync: there is no predicted tile, so there is no green F5 marker to diverge.
  At high latency the glide holds at the 1-tile cap (paced by the confirm rate). The A↔B switch is LIVE
  (F5, no restart) and re-anchors the newly-active driver from the current render position so the avatar
  does not pop.
- **C — full server follow (rejected, not built).** The local player treated like a remote entity:
  confirmed tiles only, buffered interpolator, playout delay → laggy. B is NOT C — B leads early on input;
  C lags. Do not build C and do not call B "follow the server."
- **D — UO client-driven (UO1, opt-in via the F6 render-mode cycle → `UoClientDriven`).** Ultima-Online's
  proven model: **instant client prediction + the server FOLLOWS the client's per-step requests
  (accept/reject)** instead of auto-pacing. Reuses model A's `LocalPlayerPredictor` (predict + tick-grid
  stepping + step-seq reconcile) for the local render, but additionally (1) declares the session client-driven
  to the server via `MovementModeMessage(true)` so `StepHeldMovementIntents` stops auto-pacing that entity
  (re-sent on (re)login/respawn; `false` on leaving), and (2) emits one `StepCommitRequest(++seq, dir)` per
  predicted accepted step (the S103 commit-step path) so the server advances the entity only on accepted
  commits. A rejected commit snaps via the predictor's existing `RecipientStepSeq` reconcile. The client still
  sends `MoveIntent` for stop/keepalive/facing; the server just ignores it for *pacing* while the flag is set.
  Anti-cheat is free: the cooldown gate + the commit's no-speedhack borrow cap the step rate regardless of how
  fast the client requests, so no fastwalk throttle is needed. The commit anti-cheat floor
  (`CommitAcceptFraction = 0.5`) is shared with S103 and **unchanged** — flagged for follow-up tuning (a
  separate `ClientDrivenAcceptFraction`) only if a normal-cadence step ever spuriously rejects under latency.
