# RENDER1 — trim render modes to Cosmetic + UO, default to UO, clarify labels

PRODUCTION on `review/tile-step-todo`. The F6 render-mode cycle has grown to 4 confusing modes. User decision:
**keep only `CosmeticLead` + `UoClientDriven`, drop `Predicted` + `AcceptDeny`, and boot into `UoClientDriven`.**

**Depends on NET2** (same files — `MmoClient.cs`, `MmoClientRoot.cs`). Do this AFTER NET2 lands to avoid churn.

## Rationale (for the commit message)
- `UoClientDriven` is the keeper (feels right at latency; getting loss-robust via NET2 + Stage 4).
- `CosmeticLead` stays as the smooth-low-latency comparison for now.
- `Predicted` is REDUNDANT with UO — it's the predictor with NO commits + server still held-pacing, i.e. the
  version that snaps at latency. A worse UO. Drop it.
- `AcceptDeny` is the no-prediction server-only mode, rejected early. Drop it.
- This is an interim clarity trim; the netcode milestone's Stage 5 collapses to a single model anyway.

## What to do
1. **Enum + cycle:** remove `Predicted` and `AcceptDeny` from `MovementRenderMode` (`MmoClient.cs:13`) and from
   `RenderModeCycle` (`MmoClientRoot.cs:~943`). Cycle becomes `[CosmeticLead, UoClientDriven]`. The render mode is
   CLIENT-LOCAL (not on the wire), so renumbering the enum is safe — but check for any persisted/default reads.
2. **Default:** change `_renderMode = MovementRenderMode.CosmeticLead` → `UoClientDriven` (`MmoClient.cs:116`), and
   the F6 button seed. (User accepts UO-not-yet-loss-robust as the boot mode — they're the only tester; NET2 lands
   first so it's robust under typical loss by the time this ships.)
3. **KEEP the underlying code:** `LocalPlayerPredictor` (UO uses it — keep `UsesPredictor`), the cosmetic driver
   (CosmeticLead uses it). Only remove the *standalone* `Predicted` mode and the `AcceptDeny` (LeadEnabled=false)
   cosmetic variant + their routing branches. Do not delete the predictor or cosmetic classes.
4. **Labels + descriptions:** rewrite the F6 mode labels + the per-mode caption (the UO2 contextual caption) so the
   two remaining modes are self-explanatory, e.g. "UO (client-driven — instant, server follows your steps)" and
   "Cosmetic (smooth glide, no banking — best at low latency)". Keep the contextual show/hide (model-B-only rows
   appear only under Cosmetic).
5. **Tests:** update/remove any test referencing `Predicted`/`AcceptDeny` modes; keep predictor/cosmetic unit
   tests (they test the drivers, not the mode enum). TEST1 must stay green (its `Predicted` parameterisation, if
   any, re-targets to the kept modes — do not weaken assertions).

## Gates
- `run-checks.cmd` green + `godot-build.cmd` clean. **Do NOT run `stop-mmo`/kill a live session** — if a DLL lock,
  report + leave to the Orchestrator. If `git` denied, leave work + `review/review-request-render1.md`.

## Standing rules
One discrete revertable commit referencing this task; delete the todo in it. **Safe Local Execution**. Reverting
restores the 4-mode cycle + CosmeticLead default.

## Acceptance
F6 cycles only `CosmeticLead` ↔ `UoClientDriven` with clear self-explanatory labels; client boots into UO;
Predicted/AcceptDeny gone from the UI + enum but the predictor/cosmetic code intact; gates + TEST1 green.
