# S — flat-dash END position can permanently fail to replicate (missing action-end StateRevision bump)

From the Phase D implementer tripwire + independent review (F1, MEDIUM). The one genuine framework gap the
"actions are cheap" claim hit: design §2.4's action→rest re-publish exists only via side effects that flat
dashes don't trigger.

## Mechanism (verified in code)

- Force-include while dashing: `forceMoving (Velocity≠0) || _actionExecutor.IsActive || !HasAckedCurrentRevision`
  (`GameServer.cs:~1104-1112`). The comment relies on "SnapToGround's StateRevision bump" for the landing —
  true for the JUMP (VerticalOffset changes airborne→0), a NO-OP for a grounded dash (`WorldEntity.SnapToGround`
  bumps only if VerticalOffset changed; a JumpHeight=0 dash keeps it exactly 0).
- `ApplyResolvedMove` bumps StateRevision only on a ROUNDED-TILE crossing.
- `ServerActionExecutor.Step` removes the instance on its final tick BEFORE the snapshot build → `IsActive`
  already false; a standstill-triggered dash has Velocity 0.

## Concrete scenario (reviewer-constructed)

Standing player at x=7.6 dodge-rolls east (0.5u/tick): 8.1 → 8.6 → 9.1 → 9.6 → 10.1. Final tick:
round(9.6)==round(10.1)==10 → no tile cross, no Z change, IsActive false, Velocity 0 → the final 0.5u never
replicates. Remote viewers hold the roller at 9.6 while server truth is 10.1, delta'd out INDEFINITELY (a
ghost offset until the roller next moves/turns/takes damage). Hits ~50% of standstill rolls / ~33% of
standstill charges. Dash-into-wall is safe (pinned ticks); walking-in triggers self-heal via the keepalive
stop-edge bump. The dasher's own client is unaffected.

## Fix (small, executor/GameServer action-end seam)

Bump the entity's StateRevision when an action instance ENDS (in `EndInstance` / the executor's end path, or
a GameServer hook on action completion) — the `MarkRepositioned`-class bump the stop-edge and SnapToGround
paths already use. Alternative: keep force-including the entity for ONE tick past the action's end.

## Acceptance criteria

- A standstill-triggered dodge-roll/charge whose final tick does NOT cross a rounded tile still re-publishes
  its final position to viewers (headless test: viewer's last received position == server truth after the
  dash ends + one snapshot).
- Jump landing replication unchanged; no per-tick bandwidth increase for idle entities.
- Netcode → full rigor: headless repro test first, independent review.

Do this BEFORE the Phase E live feel-test (a remote player watching a dash is exactly the E test scenario).
Builds on [[movement-actions-framework]].
