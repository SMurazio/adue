using Mmo.Shared.Domain;

namespace Mmo.Server.Runtime;

// MONSTER-BEHAVIOR P3 (docs/monster-behavior-design.md): the monster BRAIN seam. A behavior decides WHERE a monster
// wants to go (a continuous WorldVector target) and WHEN it may move / aggro / attack — the state machine + timers —
// expressing its motion ONLY through the per-tick IMonsterLocomotion ("body") it is handed each step. Mirrors the
// IMonsterLocomotion (P1) precedent, now for the brain: GameServer owns a registry of behaviors keyed by
// MonsterType.BehaviorId and resolves a monster's behavior per type (spawn → Register, death → Forget, tick →
// StepMonster), so a second brain (P4 SkirmisherBehavior for the gnoll) slots in as a new registry entry with no change
// to the routing. Ship ONLY BasicRoamerBehavior this phase — the slime/gnoll roam/chase/leash brain.
//
// The behavior owns ALL per-monster AI state (keyed by entity id) internally; GameServer only drives the lifecycle
// (Register/Forget) + the per-tick step and never reaches into that state. Diagnostic/test accessors
// (TrackedCount / TryGetPhase / TryGetHome / TryGetTarget) live on the CONCRETE behavior, NOT on this seam — they are
// implementation-specific visibility for the basic-roamer's tests, not part of the brain contract every behavior owes.
public interface IMonsterBehavior
{
    // Register a freshly spawned monster (its spawn Position becomes the leash home) with an initial randomized pause.
    void Register(WorldEntity monster, uint serverTick, uint pauseMinTicks, uint pauseMaxTicks, uint aggroScanIntervalTicks);

    // Drop a monster's AI state (on despawn/death). No-op if untracked.
    void Forget(ulong monsterId);

    // Step ONE tracked monster for this tick: decide/advance its state machine and, when moving, drive the handed-in
    // `locomotion` toward its continuous nav target on the move cadence (`cooldownTicks`). Returns true iff the monster
    // committed a move this call (diagnostic only — replication rides StateRevision, not this flag).
    bool StepMonster(WorldEntity monster, uint serverTick, uint cooldownTicks, in MonsterAiTunables tunables, IMonsterLocomotion locomotion);
}

// SLIME-SLAM ROOT+LEAP (todo/S-slime-slam-root-and-leap.md): the CAST PLAN a successful slam cast hands back to the
// brain — everything the brain needs to run the root-and-leap channel WITHOUT knowing any locomotion/telegraph
// internals (it stays locomotion-agnostic; GameServer computes all three from the type's windup + hop-airborne knobs
// and the scheduler's wire-quantized shape):
//   * Origin        — the LOCKED, wire-QUANTIZED telegraph center (the exact circle clients see and resolve tests
//                     against — the leap aims here so the landing visually matches the drawn center).
//   * LeapStartTick — the tick the leap should BEGIN so its arc LANDS exactly on ResolveTick (GameServer's timing
//                     math; strictly > the cast tick). The brain stays ROOTED until this tick, then fires the leap.
//   * ResolveTick   — the telegraph's absolute resolve tick; the channel (root + no-melee) spans cast..ResolveTick.
public readonly record struct SlamCast(WorldVector Origin, uint LeapStartTick, uint ResolveTick);

// MONSTER-BEHAVIOR P3 (docs/monster-behavior-design.md): the behavior's per-tick INPUT contract — the tick-quantised
// per-type AI config every behavior reads. Lifted out of the (former) MonsterRoamAi.Tunables to a top-level type so it
// is SHARED by all IMonsterBehavior impls (it is the seam's input, not one impl's private nested record). Built by
// MonsterTypeRegistry.BuildTunables from the live MonsterType each tick. CONTINUOUS MIGRATION (Phase 8): the navigation
// ranges are EUCLIDEAN tile-unit FLOATS (see the conversion table in BasicRoamerBehavior's header); pause/cooldown/scan
// stay TICKS. Fields/semantics are identical to the former nested Tunables — this is a pure lift + rename.
public readonly record struct MonsterAiTunables(
    double RoamRadius,
    uint PauseMinTicks,
    uint PauseMaxTicks,
    double AggroRadius,
    double DeaggroRadius,
    double ChaseLeash,
    double AttackRangeUnits,
    int AttackDamage,
    uint AttackCooldownTicks,
    uint AggroScanIntervalTicks,
    // MONSTER-BEHAVIOR P4 (docs/monster-behavior-design.md): the wounded-flee threshold as a FRACTION of MaxHealth in
    // [0,1]. 0 = never flee (every BasicRoamer type; the BasicRoamer brain ignores this field entirely). A
    // SkirmisherBehavior reads it: while Chasing, if FleeHealthPct > 0 AND Health <= FleeHealthPct*MaxHealth, it RUNS
    // AWAY from the target instead of approaching/attacking. NOT tick-quantised (a fraction, not a duration); clamped
    // to [0,1] by BuildTunables. NOT a live F1 knob this phase (behavior-specific — see the design) so NOT on the wire.
    double FleeHealthPct = 0d,
    // MONSTER-BEHAVIOR P5 (docs/monster-behavior-design.md): the CHARGE ability config consumed by the brain's charge
    // trigger (a SHARED ability — config-gated, not skirmisher-specific). ChargeEnabled = the type composed "charge" AND
    // a positive cooldown (BuildTunables computes it; false for every non-charger -> the trigger block is inert ->
    // BasicRoamer/slime byte-identical). ChargeDistanceUnits/ChargeTriggerRangeUnits are the dash length + the max
    // target distance that fires a charge (world units, fractional). ChargeCooldownTicks is the tick-quantised re-charge
    // gate the EXECUTOR's CanStart enforces (carried for the GameServer wiring + test visibility). All default to the
    // no-charge sentinel so a positional construction (the perf/headless tests) keeps charge inert with no extra args.
    bool ChargeEnabled = false,
    double ChargeDistanceUnits = 0d,
    double ChargeTriggerRangeUnits = 0d,
    uint ChargeCooldownTicks = 0u,
    // TELEGRAPH T1: the SLAM ability config consumed by the brain's slam trigger (a SHARED ability, config-gated like
    // the charge). SlamEnabled = the type composed "slam" AND a positive cooldown (BuildTunables computes it; false
    // for every non-slammer -> the trigger block is inert -> byte-identical). SlamCooldownTicks is the tick-quantised
    // re-cast gate the brain's OWN per-monster NextSlamTick enforces (a slam is a scheduled world event, not an
    // executor action, so there is no executor cooldown clock to lean on the way the charge does). The shape/windup/
    // damage stay on the TYPE — GameServer's TryBeginMonsterSlam reads them at cast; the brain only owns the WHEN.
    // Defaults keep slam inert for positional/older constructions, matching the charge fields' convention.
    bool SlamEnabled = false,
    uint SlamCooldownTicks = 0u);
