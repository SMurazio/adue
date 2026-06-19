# Local-Player Movement Prediction (design — S53)

Decided 2026-06-19 after play-test confirmed the no-prediction press→confirm→tween latency feels sluggish
even at default speed. Lifts the "no client prediction" guardrail for the **LOCAL PLAYER ONLY** (a measured
exception, per networking-design-plan §2). Remote entities stay pure interpolation. The server stays fully
authoritative — this is a client-side *guess + correct*, no wire/server change.

## Why it's tractable here
The client already holds everything the server uses to step the local player: the **blocked map** (S42,
regenerated locally) and the entity's **step cadence** (S51, sent per-entity). With the same inputs +
rules, the client's prediction equals the server's result **except for timing** — so divergence is rare
(start/stop boundaries, a mid-move speed change, or a future teleport/knockback), and reconciliation is
mostly a no-op.

## Model: mirror the server step loop, re-base off confirmations
1. **Predict.** Run a local copy of the server's step logic for the local entity only: while the held
   intent is Moving and the local step cooldown has elapsed, step one tile in the intent direction,
   validating against the **local blocked map** (same `IsWalkable`/diagonal rules). Render this
   **predicted** position immediately (the snappy part). Predict the **first step on keydown** and the
   **stop on keyup** — no round-trip wait.
2. **Re-base on each authoritative self-snapshot.** A snapshot carries the server's confirmed tile (+
   server tick). Treat it as the **anchor of truth**: set the confirmed position to it, then re-project
   forward by the steps that *should* have occurred between the snapshot and now under the current held
   intent + cadence. In the common case the re-projected position equals the current prediction (no visible
   change). If they differ (server didn't advance — it blocked, hadn't started, or stopped), the prediction
   **corrects toward the anchor**.
3. **Correction render.** Small correction (≤ ~1 tile): **fast-blend/tween** to it (no visible snap). Large
   (teleport/knockback/desync): **snap**. Never rubber-band on the steady path.
4. **Stop handling.** On keyup, stop projecting forward immediately; hold at the predicted-stop tile and
   converge to the server's confirmed stop tile when it lands (they should agree within the latency).

## Interactions (must handle)
- **S51 speed:** predict at the entity's *current* cadence; on `MovementSpeedChanged`, adopt the new
  cadence for prediction immediately.
- **Harvest adjacency:** the server still resolves interactions from *its* authoritative position. The
  client must NOT let a *predicted-but-unconfirmed* tile authorize an interaction the server will reject —
  either gate interact targeting on the confirmed tile, or pair with the server-side interact grace-window
  idea. Flag this; don't silently let prediction reintroduce the "too far" mismatch.
- **Click-to-move (S52):** it drives the same held intent, so prediction applies for free; the path-driver
  still advances on confirmed tiles (leave it).

## The bar (tests + revert)
- **Convergence/correctness test (must):** drive predicted steps, inject a server disagreement (a step the
  server rejects, or a mid-move speed change), and assert the predicted position **reconciles exactly to
  the server's** and continues correctly. Plus a **no-divergence steady-state** test: with matching map +
  cadence, prediction equals the server tile-for-tile and **no correction fires** (proving we don't
  rubber-band normal play).
- **Revert criterion (S47b lesson):** if reconciliation visibly rubber-bands in normal play, back it out
  like S47b rather than ship a worse feel than no-prediction. Local-player-only keeps the blast radius
  contained.

## Scope
Client only (`Mmo.Client.Core` prediction/reconciliation for the local entity + the local interpolator
driving from predicted tiles; Godot wiring). No server/protocol/AOI/remote-entity change.
