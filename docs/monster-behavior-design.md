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

## 1.5 The replication guardrail (the "fun but can't be insane" constraint — LOCKED)
Monsters are **server-run and REPLICATED** to clients that **extrapolate** a monster's position forward along its
replicated velocity (the new default), or interpolate. So a behavior is only as good as it REPLICATES — its *motion*
must be replication-sane, or the remote view snaps/jitters. Hard rules every behavior/locomotion obeys:
- **Continuous motion must be velocity-coherent.** The replicated `Velocity` should point where the monster is
  actually going, so extrapolation is accurate (a Glide walker extrapolates perfectly; the hop carries `Velocity 0`
  but is force-included densely per tick, so it tracks). A behavior may NOT produce per-frame erratic/unpredictable
  wiggle — that defeats extrapolation and reads as jitter.
- **Anything sudden goes through the shared action executor.** A blink/leap/charge/dash is a discrete, authoritative
  `MovementActionDef` (deterministic, replicated, the client is told) — NOT a raw teleport the client can't anticipate.
- **No client-side monster simulation.** The brain runs at the SERVER tick rate and expresses itself ONLY through the
  locomotion (smooth motion) + abilities (discrete actions). The client never predicts a monster; it just renders the
  replicated position/velocity + action.

This is exactly WHY the behavior model below is **composition from a curated library of replication-safe primitives**,
not arbitrary per-monster scripting: "fun" comes from mixing + tuning safe building blocks, so a content author
*cannot* author an un-replicable ("insane") monster. The expressiveness is in the COMBINATIONS, bounded by safe rails.

## 2. Recommended structure — DATA-COMPOSED code primitives (resolves decisions 1 + 2)
**The model (locked):** a monster TYPE is **DATA** that COMPOSES a curated **code library** of replication-safe
primitives. The behavior/locomotion/ability LOGIC is code (vetted, replication-safe, type-safe, testable); a monster
*type* is a data manifest that names which primitives to combine + the stats/tuning that parameterise them. New fun
monster = a data entry mixing existing primitives (+ tuning); a genuinely new capability = one new primitive added to
the code library (then any type can compose it). This is the same data-driven discipline the F1 tuning tab already
follows (the NUMBERS dimension), extended to the COMPOSITION dimension — and it keeps the replication guardrail (§1.5)
because the only building blocks are safe code primitives, not free-form script.

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
- **P0 — Data manifest for monster types (composition + tuning).** Move `MonsterType` from the code-seeded registry to
  a loaded data manifest that names primitives (locomotion/behavior/ability ids) + stats + tuning, with schema
  validation + the F1 live-tuning still on top. Can lead OR trail P1–P3 (the composition record is the same either
  way) — sequence it whenever the authoring win is wanted. Until then, types are composed in code with the SAME shape.
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

## 5. Decisions (LOCKED with the user) + what stays open
- **Behavior model — LOCKED:** composition from a curated CODE LIBRARY of replication-safe primitives, selected per
  type. NOT arbitrary behavior-scripting (a script engine is over-build AND a replication risk — see §1.5) and NOT
  pure-code-per-type (too slow for authoring many fun monsters). A behavior tree can later live UNDER one
  `IMonsterBehavior` primitive if a single monster needs designer-authored complexity, without disturbing the others.
- **Code vs data — LOCKED: DATA** for the COMPOSITION (a monster type is a data manifest naming primitives + stats +
  tuning) — so making a fun new monster is a data edit, not a build. The behavior/locomotion/ability LOGIC stays CODE
  (you cannot safely data-drive logic + keep replication sane). Migrate `MonsterType` from the code-seeded registry to
  a loaded manifest in its own phase (P0 below); the composition model is identical either way, so the seams (P1–P3)
  don't block on the loader.
- **Packs / group behavior — DEFERRED** (the user wants packs eventually, not now). Out of P1–P6. The behavior seam is
  kept PACK-READY: `IMonsterBehavior` decisions can later read nearby allies / a shared squad blackboard, and a
  group-coordination layer feeds the individual brains — so deferring it doesn't paint us into a corner.
- **Replication guardrail — LOCKED** (§1.5): behaviors express motion only via velocity-coherent locomotion + discrete
  executor actions; no client-side monster sim; no un-anticipatable teleports.

Still open (smaller, decide as we build):
- **Shared brain scaffolding:** base-class-with-overrides vs injected helpers (how much slime & gnoll actually share) —
  settle at P3 when the seam is extracted.
- **Manifest format + when:** the data manifest format (JSON/other) + whether P0 (loader) leads or trails the seams.
- **Visual/animation replication:** fold the deferred per-type visual id + action `AnimationId` in at P6 or sooner.

> The existing per-type tuning (the data-driven F1 Monster tab) already gives the NUMBERS dimension; this design adds
> the BEHAVIOR dimension on top of it. Builds on `movement-actions-design.md` (the shared executor/abilities) and the
> continuous-movement migration.
