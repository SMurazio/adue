using Mmo.Shared.Domain;

namespace Mmo.Server.Runtime;

// BOSS-3 (P2 SUNDER, docs/boss-encounter-sunderer-design.md): the SPLINTER's brain — a deliberately MINIMAL
// IMonsterBehavior (the InterposerBehavior sibling; it shares its "seek an encounter-supplied point and glide,
// harmless" shape). Each think-tick it steers toward ONE continuous target the encounter supplies: the NEAREST living
// participant's position (BossEncounterEngine.TryGetSplinterTarget). It NEVER harms a player itself — the POP (~12
// damage + self-despawn once within 1u of a participant) is ENCOUNTER-DRIVEN (the engine's per-tick splinter pass,
// where the PlayerDamageGate seam lives), so this brain stays a pure locomotion driver behind the injected delegate.
// It moves ONLY through the handed-in locomotion (the "splinter" type selects "glide" for a smooth 1.2 u/s creep), so
// its motion replicates + extrapolates for free with NO protocol change; it dies to anything through the shared
// monster-damage path (the tether orbit-sweep clears the ring — the S7 vulnerability). A dedicated class (not a reused
// InterposerBehavior) matches the codebase's one-class-per-brain pattern (BasicRoamer / Skirmisher / Interposer) and
// keeps the shipped P1 interposer untouched; a shared "seek-a-point" brain is a fair future refactor if a third such
// add appears (rule of three).
public sealed class SplinterBehavior : IMonsterBehavior
{
    // Resolve the splinter's current target (the nearest living participant's world point). Returns false when the
    // encounter has nothing to seek (not active / no live participant) — the splinter then holds position. `splinter`
    // is the steering monster (its position picks the nearest participant), mirroring InterposerBehavior's `drone`.
    public delegate bool TryGetTargetDelegate(WorldEntity splinter, out WorldVector target);

    private readonly TryGetTargetDelegate _tryGetTarget;
    private readonly HashSet<ulong> _tracked = [];

    public SplinterBehavior(TryGetTargetDelegate tryGetTarget)
    {
        _tryGetTarget = tryGetTarget ?? throw new ArgumentNullException(nameof(tryGetTarget));
    }

    public int TrackedCount => _tracked.Count;

    // A splinter neither pauses nor scans — the timing params are irrelevant; just record the id.
    public void Register(WorldEntity monster, uint serverTick, uint pauseMinTicks, uint pauseMaxTicks, uint aggroScanIntervalTicks)
        => _tracked.Add(monster.Id);

    public void Forget(ulong monsterId) => _tracked.Remove(monsterId);

    // One think-tick: aim at the nearest participant and glide toward it (the locomotion gates the move cadence + faces
    // the heading; the resolver slides/stops it at walls). No target, or already ON it → Stop. Returns true iff a move
    // committed (diagnostic only, like the other behaviors). The POP is the encounter's, not this brain's.
    public bool StepMonster(
        WorldEntity monster, uint serverTick, uint cooldownTicks, in MonsterAiTunables tunables, IMonsterLocomotion locomotion)
    {
        if (!_tracked.Contains(monster.Id))
        {
            return false;
        }

        if (!_tryGetTarget(monster, out var target)
            || (target - monster.Position).Length <= HopLocomotion.ProgressEpsilonUnits)
        {
            locomotion.Stop(monster);
            return false;
        }

        return locomotion.Advance(monster, target, serverTick, cooldownTicks) == HopResult.Moved;
    }
}
