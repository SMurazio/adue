# S106b — Move-speed dropdown should reflect the LIVE speed on F6 reopen (not reset to walk)

PRIORITY N. Cosmetic dev-tool wart found by the S106 independent review (2026-06-21).

## Issue
`PopulateMoveSpeedDropdown` (MmoClientRoot.cs ~1116/1782) runs on EVERY F6 open (Clear + rebuild + `Select(default
walk)`), not "lazily once" as the comments claim. So after picking a non-walk speed, reopening F6 visually snaps
the dropdown back to "walk" (no `/speed` is sent — the player stays at the picked speed — but the dropdown now
mislabels the live speed). Also: a non-default server base (`StepCooldownMs` whose tick count ∉ {1,2,3,4,5,6,8})
has no 1.0x option, so the preselect falls to the fastest entry.

## Fix
- Preselect the option matching the LIVE local-player speed (derive from the local entity's current
  `StepCooldownMs` / cadence) instead of always the default walk, so reopening reflects reality.
- Correct the "built once / lazily on first open" comments (it rebuilds each open).
- Optionally always inject `baseWalkTicks` into the candidate set so a 1.0x entry always exists.

## Acceptance
Reopening F6 after selecting a non-walk speed shows the dropdown on the CURRENTLY-active speed. Gates green.
