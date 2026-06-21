# NET3 — authored-tick command processing (the references' "input seq + replay"), loss fix

PRODUCTION on `review/tile-step-todo`. **Kills the UO-mode loss desync** that NET2 (delivery) did not. This is the
canonical fix — Gambetta ("client prediction = input seq + replay"), Valve/Source ("process commands by their
timing"), and netfox (restore→replay→record) ALL do the same thing: **the server replays buffered commands at
their own authored time and never rejects a backlog.** (Two prior attempts — UO5 stall-counter, NET2
delivery-only — failed; this one is references-backed, not improvised. VERIFY before claiming fixed.)

## The bug (precise)
NET2's redundant window recovers a lost commit, but it arrives **bundled** with the next (`[C2, C3]` in one
packet). The server applies both at the **receive tick** gated by the real-time cooldown: `C2` accepted →
`_nextEligibleTick = receive + cooldown` → `C3` REJECTED ("too early") → never confirmed → the prediction stays
ahead → desync. The cooldown gate keys eligibility on **receive time**, not the command's **authored time**.

## The fix: apply each commit at its AUTHORED tick
1. **Client stamps each commit with the tick the PREDICTOR banked that step at.** CRITICAL (the spike's
   clock-mismatch lesson): use the SAME tick the predictor used to advance the step (`AdvanceOneStep`'s step
   tick), NOT a separately-sampled `EstimateTick`/wall-clock — or the server's authored-tick application won't
   match the prediction and you'll reintroduce snapping. Extend `StepCommitBatchMessage` (NET2) to carry the
   authored tick per commit (head tick + per-window-entry tick deltas, like the seq deltas). Version bump.
2. **Server keys the cooldown schedule on the AUTHORED tick, not receive time.** Process the in-order window
   forward: `C2`(authored T+3) advances the eligible schedule to T+6; `C3`(authored T+6) is then **accepted**.
   Same backlog, no rejection — the server's `StepSequence` reaches the prediction's. Apply via the existing
   `TryCommitStep`/`TryStep` step body but gate/schedule on the authored tick.
3. **NO rollback.** The redundant window delivers recovered commits IN ORDER (sorted, deduped), so this is pure
   FORWARD replay at authored ticks. The genuine out-of-order case (a commit authored < last-applied) is what the
   window prevents; if it somehow happens, drop/clamp it gracefully (full rollback for reorder is the deferred
   Stage 4 — out of scope here).
4. **Anti-cheat (preserved, on authored ticks):** clamp authored ticks to a recent window
   `[serverTick - W, serverTick + smallLead]` (reject far-past/future); enforce cooldown SPACING on authored
   ticks (a commit's authored tick must be ≥ the prior accepted commit's nominal end = prior authored + cooldown)
   so a client cannot claim steps closer than cadence — same anti-speedhack as today, just measured on authored
   ticks. The schedule still cannot run ahead of real-time.
5. **Normal play unchanged:** with no loss, one commit arrives per cadence at its authored tick = today's
   behavior. (This is what makes it safe — like NET2/the cooldown gate, it can't regress no-loss play.)

## VERIFY before claiming fixed (required — two prior misses)
- **TEST1 loss-invariant (new):** drive the REAL predictor + REAL server step path through a dropped-then-
  recovered-in-order commit; assert the recovered commit is **accepted at its authored tick** (not rejected), the
  server `StepSequence` reaches the predicted `StepSequence`, and there is **no speed-up** and no permanent lead.
  Also keep a no-loss case green (no behavior change).
- The Orchestrator will additionally drive a client under **clumsy** (10% drop) and watch the step-seq converge.
- Do NOT weaken any existing TEST1 invariant.

## Gates
- `run-checks.cmd` green + `godot-build.cmd` clean. **Do NOT run `stop-mmo`/kill a live session** — if a DLL lock,
  report + leave to the Orchestrator. If `git` denied, leave work + `review/review-request-net3.md`.

## Standing rules
One discrete revertable commit referencing this task; delete the todo in it. **Safe Local Execution**. Builds on
NET1/NET2 (keep the redundant delivery — it feeds the in-order backlog this stage applies).

## Acceptance
A dropped-then-recovered UO commit is applied at its authored tick and accepted (not rejected); the server reaches
the predicted step-seq with no speed-up/desync under typical loss; no-loss play unchanged; TEST1 (incl. the new
loss-invariant) + gates green. Verified under clumsy before declaring the desync fixed.
