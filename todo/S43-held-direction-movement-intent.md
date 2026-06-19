# S43 — Held-direction movement intent (replace MoveStep streaming)

Severity: should-fix (correct model). **Replaces N21** (which tuned the redundant send-rate; this
deletes the redundancy). Full design + rationale: `docs/movement-input-model.md`. Movement is sensitive
(this is the system the freeze fix touched) — implement to the design exactly and surface any fork.

## What (implement the model in `docs/movement-input-model.md`)

1. **Protocol (bump from v14):** replace `MoveStep` with **`MoveIntent(uint Sequence, bool Moving,
   Direction8 Direction)`**, client→server, **reliable-ordered**. (`Moving=false` = stopped; Direction
   ignored then.) Codec read/write symmetric; remove `MoveStep` from the wire. Version bump per convention.
2. **Client:** stop the per-tick `MoveStep` stream (`SendHeldMovement`). Send `MoveIntent` **on change**
   (keydown / keyup / direction change) **plus a keepalive** resend of the current intent every ~500 ms
   (named constant). Update **both** clients (Godot + web debug). No client-side prediction — rendering
   stays confirmed-state tween as today.
3. **Server:** per-session intent state `{ moving, direction, lastSeq, lastIntentTick }`. On `MoveIntent`:
   reject `Sequence <= lastSeq` (stale); else update state. Each tick, for every session whose intent is
   `moving` and whose **step cooldown has elapsed**, attempt one tile step in `direction` using the
   **existing step validation** (bounds, walkability, per-entity cooldown). Blocked target → no step, keep
   intent. **Safety timeout:** if a `moving` session sends no intent for ~1 s (named constant), clear it
   to stopped. The tile-step itself is unchanged — only its trigger changes from a queued MoveStep to the
   held intent.

## Scope / guardrails
- Server stays authoritative; **no prediction**; tile-stepping semantics unchanged. Only the input
  representation changes. Do NOT add prediction/reconciliation.
- Keep the per-intent sequence for stale-input rejection (anti-cheat).
- Don't expand scope (no diagonal-vs-cardinal changes, no speed changes, no path-finding).

## Files
- `src/Mmo.Shared/Protocol/` — `MoveIntent` message, codec, version bump; remove `MoveStep`.
- `src/Mmo.Server/Runtime/` — intent state on the session; `GameServer` intent handler + per-tick
  intent-stepping loop; keepalive timeout. (Reuse the existing step/cooldown/validation code.)
- `src/Mmo.Client.Core/` + Godot + web — send intent on change + keepalive; remove the MoveStep stream.

## Workflow constraint
You run under accept-edits but **cannot run scripts** (build/test/stress denied) — the Orchestrator runs
all verification. Safe Local Execution is a hard rule (company-managed/Defender). Don't commit; leave for
review.

## Tests (Orchestrator runs them)
- Held intent steps the entity at the cooldown cadence; a blocked target tile stops it (intent retained);
  changing direction redirects the next step.
- A `Moving=false` intent halts movement; stale (`seq <= lastSeq`) intents are ignored; the keepalive
  timeout clears a stuck `moving` intent.
- Existing movement/AOI/freeze regression tests still pass (behavior preserved; only input model changed).

## Acceptance
- `run-checks.cmd` green; protocol version bumped. A 120-client/30s stress shows **inbound `move/s` drops
  sharply** vs today (the N21 goal, achieved by the model) with **server step cadence still even — the
  freeze must NOT return** (check the `server-steps.csv` cadence clusters).
- **Human feel-check (required, deferred to Orchestrator/human):** smooth movement, responsive start/stop,
  no runaway on key release. Flag this explicitly in the review request.
- Do NOT commit — Orchestrator reviews.

## Note
The synthetic stress client also sends movement — update it to the intent model (or it can't drive load).
If that's non-trivial, surface it rather than guessing.
