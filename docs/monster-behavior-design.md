# Monster Behavior Architecture — Design (DRAFT for review)

> Status: **design-first, not built.** Mirrors the `movement-actions-design.md` approach — propose + refine the
> structure before any production code, then build it one gated + independently-reviewed phase at a time.
>
> Question this answers (from the user): "these behaviors are very *slime-like* — we wouldn't want a gnoll to behave
> the same way. How should we structure monsters?"

## 0. Where we are today (read before designing)
A monster (`EntityKind.Monster`) today is:
- **One brain for all of them:** a single shared `MonsterRoamAi` (Idle → Roaming → Chasing → Returning, + aggro
  scan / leash / melee-on-contact). Constructed ONCE in `GameServer`.
- **One body for all of them:** a single shared `HopLocomotion` (the discrete collision-valid leap), injected once
  into that AI. (`IMonsterLocomotion` is already an interface, but only one impl exists and it is not per-type.)
- **One visual for all of them:** `EntityKind.Monster` — the client renders every monster the same way; there is no
  per-type model/animation on the wire.
- **Per-type = NUMBERS only:** `MonsterType` (a named template in `MonsterTypeRegistry`) varies stats + ranges +
  hop/feel knobs + loot. The slime is the only type.

So a "gnoll" today can only be *a slime with different numbers* — it would still hop, still use the slime brain, and
still look like a slime. To make a gnoll WALK, flank, flee, use abilities, and look like a gnoll, behavior must become
**per-type**, not just tuning.

**What we can build ON (the good seams):**
- `IMonsterLocomotion` — HOW a monster traverses toward a target for one cadence. Hop today; the seam was explicitly
  designed so a `GlideLocomotion` (velocity-based continuous walk) "slots in without touching the nav state machine."
- The **action framework** (`ServerActionExecutor` + `MovementActionDef`: jump / charge / dodge, shared with the
  player). Monsters already drive it — the slime hop is now a real Jump action through the executor. So monster
  ABILITIES are a solved mechanism; what's missing is a per-type ability SET and a brain that triggers them.

## 1. The four layers (separate the concerns)
A monster = four separable layers; each should be selectable per type:

| Layer | What it answers | Today | Target |
|---|---|---|---|
| **Identity / Stats / Tuning** | how tough, how far it sees, loot, *which* of the layers below | `MonsterType` (numbers only) | `MonsterType` + selectors (BehaviorId, LocomotionId, AbilityIds, VisualId) |
| **Locomotion ("body")** | HOW it moves to a target | one shared `HopLocomotion` | `IMonsterLocomotion` **per type** (Hop, Glide, later Fly/Charge) |
| **Behavior ("brain")** | WHERE/WHEN to move, when to aggro/flee/use an ability | one shared `MonsterRoamAi` | a pluggable behavior **per type** (shared scaffolding + per-type policy) |
| **Abilities ("what it can do")** | the actions it can perform | none (melee-on-contact only) | per-type set of `MovementActionDef`/attacks, triggered by the brain via the executor |
| **Visual** | what it looks like / animates as | one `EntityKind.Monster` | a replicated per-type visual id (+ the action `AnimationId`) |

## 2. Recommended structure — a STRATEGY seam (pragmatic, matches the codebase)
Keep `MonsterType` as the data + the SELECTORS, and make behavior/locomotion/abilities pluggable strategies keyed by
id (the same pattern `IMonsterLocomotion` already established, and the loot/action registries follow):

- `MonsterType` gains `LocomotionId`, `BehaviorId`, `AbilityIds[]`, `VisualId` (alongside the existing tuning).
- A **locomotion registry** (id → `IMonsterLocomotion`); `MonsterRoamAi` picks the monster's locomotion by its type
  instead of a single injected instance. Add `GlideLocomotion` (sets Velocity, integrates per-tick through the SAME
  shared collision the player uses — the continuous walk; the seam was designed for exactly this).
- A **behavior seam** `IMonsterBehavior` (the brain). The SHARED scaffolding (aggro scan via the spatial index, leash
  math, the no-progress watchdog, the locomotion call) stays reusable — as a base class or injected helpers — and a
  behavior composes/overrides only the DECISIONS that differ. The current state machine becomes `BasicRoamerBehavior`
  (the slime); richer types get their own (e.g. `SkirmisherBehavior` — chase but keep distance / flee at low HP).
- **Abilities** ride the existing `ServerActionExecutor`: each type carries an ability set (`MovementActionDef`s /
  attacks); the BEHAVIOR decides WHEN to fire one, the executor runs the HOW (shared, deterministic, the same code the
  player jump/charge uses).
- **Visual**: replicate a per-type visual id (and the action `AnimationId`, deferred from action-framework Phase C/E)
  so the client renders a gnoll as a gnoll with the right animations.

### Why this and not the alternatives (right-sizing)
- **Behavior trees / GOAP / ECS now = over-build** for 2 monster types. The strategy seam gives genuine per-type
  behavior with minimal infra and mirrors the proven `IMonsterLocomotion` precedent. A behavior tree can later slot
  *under* `IMonsterBehavior` for one type that needs designer-authored complexity, without disturbing the others.
- **A single mega-state-machine with per-type flags** would bloat into an unreadable knot of conditionals as types
  diverge. Separate behaviors keep each monster's logic legible and testable in isolation.
- Keep behaviors **code-defined** at first (like `MonsterType` today). Promote to data/config only if/when designers
  need to author monsters without a build — flagged as an open decision, not assumed.

## 3. Worked example — Slime vs Gnoll
- **Slime**: `LocomotionId=Hop`, `BehaviorId=BasicRoamer`, `AbilityIds=[]` (the hop *is* the locomotion), low aggro,
  melee on contact, `VisualId=slime`. (≈ today, just expressed through the new selectors.)
- **Gnoll**: `LocomotionId=Glide` (walks continuously), `BehaviorId=Skirmisher` (chase, but keep-distance / flank,
  and FLEE below an HP threshold), `AbilityIds=[Charge]` (uses the action-framework charge to close gaps), higher
  aggro + leash, `VisualId=gnoll`, pack-awareness as a later behavior refinement.

## 4. Phased build (each its own gated + independently-reviewed branch, like the action framework)
- **P1 — Per-type locomotion selection.** Registry of `IMonsterLocomotion` keyed by id + `MonsterType.LocomotionId`;
  the AI picks the monster's locomotion by type. NO behavior change (slime still Hop). De-risks the per-type plumbing.
- **P2 — `GlideLocomotion`** (continuous velocity-based walk through the shared collision) + a second type that uses
  it (a "walker") with the SAME brain. Proves a second body without touching the brain.
- **P3 — `IMonsterBehavior` seam.** Extract the current brain as `BasicRoamerBehavior`; select per type. NO behavior
  change yet (de-risk the seam, keep all the roam/chase/leash/livelock tests green).
- **P4 — A second behavior** (e.g. `Skirmisher`: flee-at-low-HP / keep-distance) for the gnoll.
- **P5 — Per-type ability sets** via the executor (gnoll charge; ranged/caster later). Reuses the action framework.
- **P6 — Per-type visual + animation replication** (a replicated visual id + the action `AnimationId`) so clients
  render distinct monsters + the right animations. Ties into action-framework Phase E.

Sequencing rationale: bodies before brains before abilities before looks — each phase is additive, revertable, and the
existing monster tests stay the safety net. A gnoll becomes shippable around P2–P4 (walks + skirmishes) and "feels
like a gnoll" by P5–P6.

## 5. Open decisions for the user (refine before P1)
- **Behavior model:** strategy seam (recommended) vs behavior-trees vs trait/components — how much designer
  authorability do you want, and how complex will the smartest monster get?
- **Shared brain scaffolding:** base-class-with-overrides vs fully-separate behaviors (how much do slime & gnoll
  actually share)?
- **Code-defined vs data-driven types/behaviors:** keep `MonsterType` in code (current), or author monsters in data?
- **Visual/animation replication:** fold the deferred replicated `ActionId`/per-type visual id in at P6, or sooner?
- **Pack / group behavior** (gnolls hunt in packs) — in scope for the first richer brain, or a later layer?

> The existing per-type tuning (the data-driven F1 Monster tab) already gives the NUMBERS dimension; this design adds
> the BEHAVIOR dimension on top of it. Builds on `movement-actions-design.md` (the shared executor/abilities) and the
> continuous-movement migration.
