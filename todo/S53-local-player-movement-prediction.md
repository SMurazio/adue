# S53 — Local-player-only movement prediction (the planned "measured exception")

Severity: feature (movement feel) — **HIGH RISK.** This is the design's explicit escalation: lift the
"no client prediction" guardrail for the **LOCAL PLAYER ONLY** (everyone else stays on interpolation), so
your own avatar moves the instant you input and reconciles against the server. See
`docs/networking-design-plan.md` §2 (the trigger + "Godot client only, not the web client").

> **DO NOT blind-implement.** Movement netcode is exactly where the S47b desync hid. This task needs a
> design pass + a misprediction-reconciliation test as the bar BEFORE the implement. Try the cheap lever
> first — a faster default walk speed (S51 made cadence tunable) may reduce or remove the need.

## The model (to design, then build)
The client already has everything needed to predict faithfully: the **local blocked map** (S42) and its
own **step cadence** (S51 sends the effective cooldown). So the client can run a **faithful mirror of the
server's step loop for the local player**:
1. **Predict:** on `MoveIntent`, step the local avatar immediately at its own cadence, validating against
   the local blocked map (same `IsWalkable`/cooldown/diagonal rules as the server). Tag each predicted
   step with the input sequence.
2. **Keep unconfirmed history:** a small queue of predicted steps not yet confirmed by the server.
3. **Reconcile** on each server snapshot of self: if the server's confirmed tile == the prediction for
   that point, drop confirmed history (no-op). On **mismatch** (server rejected/altered a step — a tile
   the client thought walkable, a cooldown/speed difference, a teleport): **snap the predicted position to
   the server's authority and replay** any still-unconfirmed inputs from there.
4. **Render:** predicted steps tween immediately (snappy); a reconciliation correction snaps or
   fast-blends to truth (minimize visible rubber-band).

## Hard requirements (the bar)
- **Local player only.** Remote entities are untouched (pure interpolation). No change to the server, the
  wire, or AOI. The server remains fully authoritative — prediction is a client-side *guess + correct*.
- **Convergence/correctness test (must, analogous to the snapshot convergence test):** drive predicted
  steps, inject a server disagreement (e.g. a step the server rejects as blocked), and assert the client
  **reconciles exactly to the server's position** and replays remaining inputs correctly. Plus a no-
  mismatch steady-state test (prediction matches server tick-for-tick, no correction fires).
- **Interactions to get right:** S51 per-entity speed (predict at the *current* cadence; handle a
  `MovementSpeedChanged` mid-move), the held-intent **stop** (predicted stop vs server stop), and harvest
  adjacency (the server still resolves from *its* position — prediction must not make the client think it
  can interact from a predicted-but-unconfirmed tile; or pair with the interact grace-window idea).

## Files (client only)
- `src/Mmo.Client.Core/` — the prediction + reconciliation layer for the local entity; unconfirmed-input
  queue; the local interpolator drives from predicted (not just confirmed) tiles.
- `src/Mmo.Client.Godot/` — wiring only.

## Process
1. **Orchestrator writes a short design doc** (`docs/movement-prediction-design.md`) — the predict/
   reconcile model, the correction-render policy, the test plan, the rollback-if-it-rubber-bands criterion
   — and reviews it before any implement. (Mirror the delta-snapshots-design discipline.)
2. Only then implement, with the convergence test green and a human feel-check.

## Acceptance
- Local avatar responds instantly to input (no press-delay / stop-coast); corrections are rare and not
  jarring; remote entities unchanged; server still authoritative. Convergence test green; `run-checks` +
  `godot-build` green; human feel sign-off. **Revert criterion:** if reconciliation rubber-bands visibly
  in normal play, treat like S47b — back it out and rethink, don't ship a worse feel than no-prediction.
