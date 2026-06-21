# S105 — F6 option: soften movement corrections (blend instead of hard snap)

Severity: S (movement feel — user request, "queue it as an option"). Client-only. No protocol/server change.
Live F6 toggle. Builds on S89/S102/S103.

## Why

During fast direction-spam (and at latency) the avatar can visibly JUMP. The steady disagreement correction in
`LocalPlayerCosmetic.Confirm` is already a ≤1-cadence blend (not a snap), so the visible "teleport" comes from
the remaining HARD snaps: `SnapTo` on a **commit-step reject** (S103) and `SnapOnRelease` (S91). The user wants an
option to make these corrections a smooth blend instead of a jump.

## What to build

Add a live **F6 "Soften corrections (blend, no snap)"** toggle (a client-level flag on the cosmetic driver, e.g.
`LocalPlayerCosmetic.SoftCorrections`, default OFF = current behavior; routed via a new
`MmoClient.SetSoftCorrections(bool)` like the other lead/commit settings, seeded on attach). When ON:
- The **commit-reject** path (`MmoClient.ReconcilePendingCommit` → `cosmetic.SnapTo(local.Tile, now)`) blends the
  render to the confirmed tile over ≤1 cadence instead of an instant `SnapTo`. (Add a `cosmetic.BlendTo(tile,
  now)` or make `SnapTo` honor the flag.)
- Optionally also soften any large-disagreement correction in `Confirm` so it never instant-jumps regardless of
  magnitude (today it blends, but verify there's no snap path for big corrections).
- Leave `SnapOnRelease` as its own existing toggle (don't double-handle it); this option targets the
  *involuntary* correction snaps (commit-reject, big disagreements), not the deliberate release snap.

Keep OFF as the default so current feel is unchanged until toggled. Examine the actual snap call-sites first and
soften exactly those under the flag; don't change the steady blend behavior that's already smooth.

## Tests
- `LocalPlayerCosmeticTests` / `MmoClientCommitStepTests`: with SoftCorrections ON, a commit reject BLENDS to the
  confirmed tile over a cadence (render is NOT instantly on the confirmed tile at the reject instant, and IS
  within ~1 cadence later); with it OFF, the existing instant `SnapTo` behavior is preserved.
- Hardened `run-checks` green (now `--no-incremental`); Godot build clean.

## Constraints
- Client-only; no protocol/server change. Live F6 toggle, no restart. **Safe Local Execution** binds you (scripts
  only; if a session locks `Mmo.Shared.dll`, stop via `stop-mmo.cmd`, note it). You cannot run Godot — Orchestrator
  does the live check. If your shell is denied, say so explicitly; don't claim green you didn't observe.
- Do NOT commit/push/delete the task file — leave the tree dirty + write
  `review/review-request-s105-soften-corrections.md`; the Orchestrator verifies (hardened gate) and commits.

## Acceptance
- F6 "Soften corrections" toggle, live; ON makes the commit-reject (and any large-disagreement) correction a
  ≤1-cadence blend instead of a hard snap; OFF preserves current behavior. Tests + hardened run-checks green.
