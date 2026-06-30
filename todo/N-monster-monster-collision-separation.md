# N — monster↔monster collision (server-authoritative separation, no physics)

User: "monsters are missing collision — a sphere/capsule collider around them, NO physics, just collisions between them
to avoid compenetration. Server-authoritative." Today entities collide with WALLS (ContinuousCollision vs TileGrid) but
NOT with each other, so monsters stack/overlap.

**Approach (cheap, no physics — pure de-penetration):** a server-side SEPARATION pass each tick, after movement
(StepMonsterAi + executor StepAll) and before BroadcastSnapshot. For each monster, query nearby monsters via the
spatial grid (`SpatialEntityGrid.QueryNeighborhood`); for each overlapping pair (centre distance < 2×BodyRadiusUnits),
accumulate a push-away vector (half the overlap each, so equal-mass de-penetration), then apply the summed displacement
ONCE per monster, CLAMPED against walls via the shared collision resolver (ApplyResolvedMove / ContinuousCollision) so a
push never shoves a monster into a wall. Accumulate-then-apply = order-independent + stable; 1–2 relaxation iterations.

**Decisions (defaults, user can change):**
- MONSTER↔MONSTER only (the ask). Player↔monster is a one-flag extension (include players in the pass) — offered.
- Collision radius = the shared `BodyRadiusUnits` (0.5) for both wall + monster-monster, so a (visually 1.4×) gnoll
  still fits where a slime does. Per-type collision radius (tie to RenderScale / a CollisionRadius field) = later tweak.
- No physics: separation changes POSITION only, never Velocity (the user was explicit — no momentum/bounce).

**Replication (netcode care):** a nudge on a MOVING monster (Velocity≠0) is force-included already; a nudge on an IDLE
one (Velocity 0, no tile cross) would be delta'd out — so after a separation nudge that moved an entity, ensure
re-inclusion (bump StateRevision like the stop-edge / SnapToGround path) so the corrected position replicates.

**Safety/stability:** exact-overlap (two monsters on the same point) → deterministic split axis (e.g. by id); cap the
max nudge/tick (no explosions); bound cost via the spatial grid (no O(n²)).

**Rigor (netcode-adjacent):** headless repro/test (two overlapping monsters separate to ≥2×radius; an N-monster cluster
de-penetrates without jitter/explosion + stays wall-valid; the nudge replicates) + independent review + a human
feel-test (monsters don't overlap, look natural, no jitter/shoving-through-walls). Pairs well with the queued
monster-dense stress (`N-phaseC-monster-dense-bandwidth-stress`). Builds on [[monster-behavior-architecture]].
