# N — monster behavior architecture (per-type behavior, not just tuning)

DESIGN-FIRST. Full proposal: `docs/monster-behavior-design.md` (review + refine before any build).

**Why:** today every monster shares ONE brain (`MonsterRoamAi`) + ONE locomotion (`HopLocomotion`) + ONE visual
(`EntityKind.Monster`); a `MonsterType` varies only tuning NUMBERS. So a gnoll would be "a slime with different
numbers" — it would hop, use the slime brain, and look like a slime. We need BEHAVIOR to be per-type.

**Proposed structure (strategy seam — see the design doc):** separate the four layers — Tuning (`MonsterType`, have
it) / Locomotion (`IMonsterLocomotion` per type; add `GlideLocomotion`) / Behavior (`IMonsterBehavior` per type;
extract the current brain as `BasicRoamerBehavior`) / Abilities (per-type set via the shared `ServerActionExecutor`) /
Visual (replicated per-type id + the action `AnimationId`). Right-sized: a strategy seam, NOT behavior-trees/ECS up
front (BTs can slot under `IMonsterBehavior` later).

**Phases (each gated + independently reviewed):** P1 per-type locomotion selection → P2 `GlideLocomotion` + a walker
type → P3 `IMonsterBehavior` seam (extract `BasicRoamerBehavior`, no behavior change) → P4 a 2nd behavior (Skirmisher:
flee/keep-distance) → P5 per-type ability sets (gnoll charge) → P6 per-type visual + animation replication.

**Open decisions (in the doc):** behavior model (strategy vs BT vs components); shared-scaffolding vs separate brains;
code-defined vs data-driven types; when to fold in visual/animation replication; pack/group behavior scope.

**Status: awaiting the user's review of the design doc** before P1. Builds on [[movement-actions-framework]] +
[[tile-continuous-cleanup]].
