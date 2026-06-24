# N — monster roam: corner-cut diagonal can livelock a Roaming monster

**Source:** independent review of `feat/living-enemies-phase1-roam` (Phase 1 leashed roam). SHIP verdict, this
is one of two flagged follow-ups (the other — attacker damage-numbers on monsters — was fixed inline).

## The bug

`MonsterRoamAi.StepTowardDestination` distinguishes "step dropped on cooldown (wait, stay Roaming)" from
"genuinely blocked (bail to Idle)" using a **terrain-only** check: `if (!_isWalkable(nextTile)) GoIdle(...)`.
That misses the **diagonal corner-cut rule** the real step enforces (`WorldEntity.IsStepWalkable`): a diagonal
into an open tile is still rejected if it would cut a wall corner. In that case `_tryStep` returns false
(blocked) but `_isWalkable(nextTile)` is **true** (the destination tile itself is open), so the AI assumes it
was a cooldown drop, stays Roaming, re-picks the same diagonal next tick, and **spins forever — the monster
freezes mid-leash against a wall corner**, defeating the believable "stroll then pause" loop. No crash, no
leash violation, replication unaffected. The existing wall test passes only because its seed avoids the trap.

## Fix (robust, no rule duplication)

Add a **no-progress timeout**: track `LastProgressTick` on `MonsterState` (set on entering Roaming + on every
successful step). In `StepTowardDestination`, on a non-progress step also bail to Idle when
`serverTick - LastProgressTick > stepCooldownTicks * 2 + margin` — i.e. it has missed more than ~2 step
windows without advancing, which a mere cooldown wait can't explain. This catches the corner-cut case (and any
other block the terrain oracle misses) without re-implementing the corner-cut rule in the AI's walkability
oracle. Add a test: a monster boxed so its only greedy route is corner-cut bails to Idle + re-pauses (does not
freeze) within the timeout.

(Alternative considered: test the diagonal side-tiles in the destination/direction picker — rejected as it
duplicates the corner-cut rule and risks drift from `WorldEntity.IsStepWalkable`.)
