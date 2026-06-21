# UO2 — make the F6 movement panel contextual to the selected render mode

PRODUCTION work on `review/tile-step-todo`. Problem (from live debugging): the F6 panel shows ALL movement
options regardless of the selected render mode, so options that are INERT in the current mode are still visible
and you can't tell what's actually active — making it impossible to isolate behavior. Fix: show only the controls
that apply to the current render mode; hide or clearly grey-out + label the rest.

**Depends on UO1** (do this AFTER UO1 lands, so the `UoClientDriven` mode + any of its options exist to be shown).

## What to build
In `MmoClientRoot.BuildMovementPanel` (MmoClientRoot.cs:855) + `OnRenderModeCyclePressed`/
`UpdateRenderModeButtonText`: make the per-row visibility depend on `_client.RenderMode`. Re-evaluate whenever the
render mode changes (the cycle button) and on panel open.

### Which options apply to which mode (verify against the code; these are the intended mappings)
- **Always shown (all modes):** the render-mode cycle button; **Net latency (ms)** (it's a client-wide sim, not
  mode-specific).
- **CosmeticLead (model B) only:** **Cosmetic lead (tiles)**, **SnapOnRelease**, **CommitStepOnRelease** (+ its
  threshold). These are model-B-only and already documented "inert otherwise" in `MmoClient` — they should be
  HIDDEN (or greyed + "(CosmeticLead only)") in Predicted / AcceptDeny / UoClientDriven.
- **Predicted / UoClientDriven:** show predictor-relevant controls if any exist (e.g. nothing extra today beyond
  the shared rows); do NOT show the model-B-only rows.
- **AcceptDeny:** the cosmetic driver with lead OFF — hide the lead/snap/commit rows too.

Prefer **hide** over disable for cleanliness, but a greyed-with-reason control is acceptable if hiding causes
layout jumps — your call, keep it simple and readable. Add a small caption under the render-mode button naming
what the current mode does (one line) so it's self-documenting.

Keep it UI-only — do NOT change any movement behavior. If a row's applicability is genuinely ambiguous, leave it
shown and note it in the review-request rather than guessing.

## Gates
- `godot-build.cmd` clean (this is a Godot-only change) AND `run-checks.cmd` green (nothing in core should
  change; confirm). If your shell is denied, say so and do NOT claim green — the Orchestrator runs the gates.

## Standing rules
- One discrete revertable commit referencing this task; delete this file in that commit. **Safe Local Execution**
  (scripts only). You cannot run Godot — the human verifies the contextual behavior live.

## Acceptance
- Selecting each render mode on F6 shows only the controls relevant to it; model-B-only rows vanish/grey in
  Predicted/AcceptDeny/UoClientDriven. A one-line caption names what the current mode does. No behavior change.
  `godot-build` + `run-checks` green.
