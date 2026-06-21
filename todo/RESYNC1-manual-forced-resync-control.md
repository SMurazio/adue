# RESYNC1 — manual Force Resync control (F6 button + Alt+R hotkey) + the shared resync primitive

PRODUCTION on `main`. **PRIORITY S — do FIRST (small; foundation for UO5 tier-2 + NET4 tier-3).**
User wants a manual "force resync" they can trigger live when the prediction desyncs under loss — both an **F6
panel button** and an **Alt+R hotkey**. See `docs/movement-loss-degradation-tiers.md`.

## What to build
1. **The resync primitive (reusable — this is the point).** Expose a single method on the local predictor
   (`LocalPlayerPredictor`), e.g. `ForceResync()`, that:
   - snaps `_predictedTile` to the **last server-confirmed tile** (the authoritative position),
   - sets `_predictedStepSeq = serverStepSeq` (re-anchor),
   - clears in-flight history + any unconfirmed/banked commits (so nothing stale replays),
   - **snaps the render** to the server tile (hard, no blend — this is an explicit resync),
   - is idempotent / safe to call when already in sync (no-op-ish).
   Build it as a clean reusable method — **UO5 (tier 2) and NET4 (tier 3) will call it**, so do NOT bury the
   logic inside a button handler.
2. **F6 button.** Add a "Force Resync" button to the F6 panel. On click → call `ForceResync()` on the local
   entity. Live, no restart (Diagnostics-are-live-toggles guardrail).
3. **Alt+R hotkey.** Global input handler → same `ForceResync()`. Live.

## Notes / scope
- Local-player only; does not touch remote-entity interpolation.
- Works in whatever mode the local predictor runs (UO mode is the one that matters; in cosmetic mode it's a
  harmless snap-to-server).
- No protocol change — this is purely client-side (snap to the data we already have).
- Diagnostic value: lets the human clear a desync on demand and confirm the resync path works, independent of
  the auto-tiers.

## Gates
- `run-checks.cmd` green + `godot-build.cmd` clean. **Do NOT run stop-mmo/gates that kill a live session
  without flagging it.** Safe Local Execution.

## Standing rules
One discrete revertable commit referencing this task; delete this file in that commit on success. You cannot
run Godot — the human verifies the button + hotkey live (press it mid-desync → green snaps onto magenta).
Emit `review/review-request-resync1.md` when done.

## Acceptance
Pressing the F6 "Force Resync" button OR Alt+R while the prediction is desynced snaps the local avatar onto the
server-confirmed position immediately and cleanly, and normal prediction resumes from there. `ForceResync()` is
a reusable predictor method (verified by a unit test: desynced predictor → ForceResync → predicted tile/seq ==
server tile/seq, in-flight cleared). Gates green.
