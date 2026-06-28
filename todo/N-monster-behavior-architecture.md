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

**Decisions LOCKED with the user (see the doc §5):**
- Behavior model = **composition from a curated code library of replication-safe primitives** (NOT a script engine,
  NOT pure-code-per-type). A behavior tree may later live under one behavior primitive if needed.
- **Data-driven COMPOSITION** (a monster type is a data manifest naming primitives + stats + tuning); behavior LOGIC
  stays code. Manifest loader = phase P0 (can lead or trail the seams; composition shape is identical in code).
- **Replication guardrail** (§1.5): behaviors express motion ONLY via velocity-coherent locomotion + discrete shared-
  executor actions; no client-side monster sim; no un-anticipatable teleports. This is what keeps "fun" from becoming
  un-replicable ("can't be insane").
- **Packs DEFERRED** (wanted eventually). Out of P0–P6; the behavior seam stays pack-ready (later group layer feeds
  individual brains).

Still open (decide while building): shared-scaffolding vs separate brains (settle at P3); manifest format + P0 timing;
when to fold in the per-type visual + action AnimationId replication.

**Phases:** P0 data manifest → P1 per-type locomotion → P2 GlideLocomotion + a walker → P3 IMonsterBehavior seam
(extract BasicRoamer) → P4 a 2nd behavior (Skirmisher) → P5 per-type abilities → P6 visual/animation replication.

**Status: design refined + locked; ready to start P1 (or P0) on the user's go.** Builds on
[[movement-actions-framework]] + [[tile-continuous-cleanup]].
