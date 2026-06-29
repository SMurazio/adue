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
    uint AggroScanIntervalTicks);
