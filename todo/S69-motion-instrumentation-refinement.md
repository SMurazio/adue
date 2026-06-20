# S69 — Refine motion instrumentation so it's trustworthy (live flush + real teleport metric + per-capture reset)

Severity: dev tooling / correctness of diagnostics. The S67/S68 motion instrumentation gave **misleading
signals** during diagnosis: (1) the live CSV froze mid-capture because the writer never flushes until close,
so reads while logging are stale; (2) the `snapCount` fires on the *divergence sawtooth* (the confirmed tile
jumping a whole diagnoal tile each step while the render glides) and on prediction lead — NOT on actual
visible stutter, so it massively over-counts; (3) `maxDivergence`/`snapCount` are session-cumulative, so a
fresh capture can't be measured cleanly. Fix all three so the tool tells the truth. Client-only; diagnostics
only — **no movement-behavior change.**

## What
1. **Live-flush the frame CSV.** Today `OpenFrameCsv` sets `AutoFlush = false` and only flushes on
   `CloseFrameCsv`, so a live read sees a stale/frozen file. Flush periodically — a few times per second
   (e.g. every ~0.25 s or every ~15 rows in `AppendFrameCsvRow`), or set `AutoFlush = true` if the per-frame
   cost is negligible for a debug log. Goal: reading `.run/client-frames-<player>.csv` while logging shows
   rows current to within a fraction of a second.
2. **Make `snapCount` count RENDER teleports, not divergence jumps.** Replace the
   `|divergence − prevDivergence| > 0.75` logic in `SampleMotionMetrics`. A snap = the **rendered position
   jumping in a single frame by far more than the normal per-frame glide** — i.e. a visible teleport. Make it
   frame-time-aware so a legitimately long frame isn't counted: e.g. compare `frameDelta` to the expected
   glide for that frame (`currentSpeed × frameSeconds`) and count a snap only when the render moved
   dramatically more than that (a sudden catch-up), or use an absolute single-frame jump threshold well above
   the max normal glide (run-diagonal ≈ 0.16 tile/frame at 60 fps) — pick a sensible default (tunable const)
   and document it. The point: `snapCount` should track **visible position jumps**, and read ~0 for smooth
   glide even on diagonals/direction-changes.
3. **Reset the motion counters on a fresh capture.** When the frame-log is toggled **on** (and/or in
   `OpenFrameCsv`), reset `_maxRenderDivergence`, `_renderSnapCount`, `_prevDivergence`/`_prevRenderPos`
   state so each capture's max-divergence + snap-count reflect *that* capture, not the whole session.
4. Keep `divergence`/`maxDivergence` as-is (they're a useful prediction-lead signal) — only the snap metric
   changes meaning.

## Diagnosis targets (context for why — not code to write)
With the fixed tool I'll capture controlled traces to answer: does a direction reversal cause a *visible*
render teleport (frameDelta spike) or just a clean glide? and why does render sit ~1 tile off the confirmed
tile when blocked/idle (a possible predictor desync on rejected steps)? The instrumentation must read
truthfully for those to mean anything.

## Constraints
- Client-only; no protocol/server/movement change; keep the per-frame hot path cheap. Don't change the CSV
  column set/order (S67 16-column) — only flushing + the snap *definition*.
- Run `.\.shared\skills\mmo-dev\scripts\run-checks.cmd` before/after (try it; if Bash denied, note + continue
  — Orchestrator runs the gates). You can't run Godot — Orchestrator runs `godot-build` and verifies live by
  driving via MCP after a relaunch (a smooth straight/diagonal/reversal run should now read snapCount ≈ 0,
  and the live CSV should advance while logging). **Safe Local Execution** binds you.

## Acceptance
- `godot-build` green; live CSV advances within ~0.25 s while logging; `snapCount` reads ~0 for smooth glide
  (incl. diagonals + direction changes) and only rises on genuine render teleports; counters reset per
  capture. Movement unchanged. Review-request → `review/review-request-s69-motion-instrumentation-refinement.md`.
  Do NOT commit or delete the task file.
