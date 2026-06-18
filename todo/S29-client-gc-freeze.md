# S29 — Eliminate the residual client GC-pause freeze (reduce client allocations)

Severity: should-fix (polish). Last residual of the long movement-stutter hunt.

## Diagnosis (from the F3 HUD, live)

After S26 (renderer), S27 (geometry batch), and the interpolation buffer fix (local
`LocalInterpolationCadenceMultiplier=1.0`, q now holds 2-3), the human still feels an **occasional
freeze** ("freezes everything, no tears"). F3 HUD at the time:

```
fps 712   frame ms last/max 1.4/150.0   draw/objects 69/2822   managed MB 5.2
gc 23/2/1   hitches 4 >18.0ms   interp q=2 cadence=150ms
```

- Not tearing (human confirmed), not interpolation (q=2-3), not frame-rate (712-1300 fps), not the
  server (proven clean).
- `gc` is climbing, including **Gen1 (2) + Gen2 (1) ≈ the ~4 hitches**. Gen0 churns harmlessly; the
  Gen1/Gen2 stop-the-world collections are the freezes. So: **client GC pauses from client
  allocations.** Client analog of the server S22 work. Now rare (≈4/session) vs the original constant
  stutter.

## Plan (PROFILE first — do not guess)

1. **Confirm + locate.** Use a sampling allocation profiler (e.g. `dotnet-trace`/`dotnet-gcdump` on
   the Godot client process, or Godot's own monitors) to find the dominant managed-allocation
   sources during steady movement. Correlate Gen1/Gen2 collections with the `hitches` from the F3 HUD.
   Likely suspects to confirm/measure (don't assume):
   - the overlay rebuilds `FormatMetrics` (allocates a `List<string>`), `FormatChat`, status, and perf
     text every 0.1s **even in normal play** (`UpdateOverlay` runs unconditionally) → steady Gen0 churn;
   - per-snapshot processing in `Mmo.Client.Core` (lists/records per snapshot, render-state copying);
   - Godot interop / per-frame `node.Position`/string churn.
2. **Reduce the dominant source(s):** reuse buffers/`StringBuilder`, only rebuild overlay strings when
   the value changed (not every tick), avoid per-snapshot `List`/record allocation, cache where safe.
   Hot/steady paths only — not cold setup.
3. Re-measure with the F3 HUD: `gc` Gen1/Gen2 should stop climbing during steady play and `hitches`
   should approach 0.

## Acceptance

- Profiler identifies the real dominant client allocation source (documented), not a guess.
- After the fix: F3 HUD shows no Gen2 (and minimal Gen1) collections during a steady movement
  session, `hitches` ≈ 0; **human confirms no perceptible freeze.**
- `run-checks.cmd` + `godot-build.cmd` green.

Note: the F3 perf HUD itself allocates (string building at 10Hz); measure with it both open and closed
so its own cost isn't mistaken for the gameplay path.
