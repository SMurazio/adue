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
