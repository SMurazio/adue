# S91 — Model B: snap to the confirmed tile on key release (kill the 150ms backward drift)

Severity: S (movement feel — user request, trying it live). Client-core only; tiny, self-contained.
Builds on S89 (`LocalPlayerCosmetic`). No protocol/server change. Model A untouched.

## The change

In model B, releasing the movement key currently TWEENS the render from the cosmetic-lead position back to the
confirmed-tile center over a full step cadence (~150ms) — a slow backward drift that feels weird. The user wants
to try an **instant snap** instead: on release, the avatar locks straight to the confirmed-tile center.

`src/Mmo.Client.Core/LocalPlayerCosmetic.cs`, `SetIntent(bool moving, ...)`, the `else` (moving == false)
branch (currently ~`:130-138`):

```csharp
else
{
    _moving = false;
    // Stop extending the lead and settle back onto the confirmed tile over one cadence. ...
    _leadTarget = null;
    StartTween(SampleInternal(now), RenderPosition.FromTile(_confirmedTile), now, _cadenceMs);
    _renderPosition = SampleInternal(now);
}
```

Replace the cadence tween with an **instant snap** to the confirmed-tile center: set the render directly to
`RenderPosition.FromTile(_confirmedTile)` with a zero/degenerate tween so `Sample(now)` returns the confirmed
center immediately (e.g. `StartTween(center, center, now, _cadenceMs)` then `_renderPosition = center`, or an
equivalent hard-set). `_leadTarget = null` stays. `_moving = false` stays (so `Tick` won't re-arm a lead).

Scope: ONLY the release branch. Do NOT change `Confirm`'s cut-tween (the disagreeing-block correction) or the
forward lead glide — the user is happy with those. Keep the cosmetic lead cap (1.0) as is.

## Tests

- New B test: glide east, then `SetIntent(false)` → assert `Sample(now)` (immediately, NOT a cadence later) is
  exactly the confirmed-tile center on both axes. (Fails before — the tween leaves it mid-glide at release time;
  passes after — snapped.)
- Keep all S89 invariants green. `AtRest_RenderSettlesExactlyOnConfirmedTile` and
  `DisagreeingConfirm_CutsToConfirmedTile_NoPersistingOvershoot` must stay green (they sample after a cadence, so
  a snap still satisfies them — verify, don't assume).

## Constraints

- Client-core only; no protocol/server/wire change; `Tile`/`LocalTile` stays confirmed-only; model A untouched.
- `.\.shared\skills\mmo-dev\scripts\run-checks.cmd` green before/after. You cannot run Godot — the Orchestrator
  runs the live re-check (release a walk in model B: avatar snaps to its tile, no backward drift).
- **Safe Local Execution** binds you. One discrete, revertable commit referencing this filename; delete the file
  in that same commit on success. Review-request → `review/review-request-s91-snap-on-release.md`.
  Do NOT commit, push, or delete the task file yourself — leave the tree dirty + write the review-request; the
  Orchestrator verifies and commits. (Same loop as S89.)

## Acceptance

- In model B, releasing the key snaps the avatar instantly to the confirmed-tile center (no 150ms backward
  drift). Forward lead, the block-cut, and at-rest exactness unchanged. New snap test (fails before / passes
  after) + all S89 invariants green; run-checks green.
