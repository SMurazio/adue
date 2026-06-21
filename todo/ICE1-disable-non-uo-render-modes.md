# ICE1 — put the non-UO render modes on ice (disable in UI, keep the code)

PRODUCTION on `main`. **PRIORITY S.** User directive (2026-06-21): "we shouldn't optimize for non-UO movement;
the other render modes we should disable for now (without removing them, just put them on ice)." UoClientDriven
is the validated, supported movement mode; the alternative(s) (CosmeticLead, and any other surviving render
mode) should be **unreachable from the UI but kept in the codebase** for later.

## Context
- `MmoClient.MovementRenderMode` enum + `SetMovementRenderMode`. Default is already `UoClientDriven`
  (MmoClient.cs ~168). `UsesPredictor` (MmoClient.cs ~34) is UoClientDriven-only; the others ride the cosmetic
  driver. (Two earlier modes were already removed — see the enum comment ~line 13.)
- The F6 panel has a render-mode selector: `_renderModeButton` — "a 2-way render-mode selector (CosmeticLead /
  UoClientDriven) cycling button" (MmoClientRoot.cs ~123) wired to `SetMovementRenderMode`.

## What to do
1. **Make the non-UO modes unreachable from the UI.** Remove/hide the render-mode selector button (and any
   hotkey/F-toggle that switches modes), so the client stays in `UoClientDriven`. The simplest clean approach:
   don't build the `_renderModeButton` row (or build it disabled with a "UO only (others iced)" note). Pick one
   and keep it tidy.
2. **Keep ALL the non-UO code** — the enum values, `SetMovementRenderMode`, the cosmetic driver, the cadence
   plumbing. Do NOT delete them; this is a UI-gating change only, so un-icing later is just re-exposing the
   control.
3. Ensure nothing forces a non-UO mode at startup and that removing the selector leaves the client cleanly in
   UoClientDriven (no dangling references / null UI handlers).
4. Leave a short code comment at the selector site noting the modes are iced (UI-disabled, code retained) and
   why, so it's discoverable.

## Out of scope
- Do NOT remove the render-mode code, the cosmetic driver, or the enum. No protocol change.

## Gates
`run-checks.cmd` + `godot-build.cmd` green. One discrete revertable commit referencing this task; delete this
file in that commit. Safe Local Execution; you cannot run Godot — the human verifies the selector is gone and
the client stays in UO.

## Acceptance
The F6 render-mode selector no longer lets the user switch to a non-UO mode (it's removed or disabled); the
client runs UoClientDriven; all non-UO movement code remains in the tree for later un-icing. Gates green.
