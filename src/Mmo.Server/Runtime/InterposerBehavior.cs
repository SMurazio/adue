using Mmo.Shared.Domain;

namespace Mmo.Server.Runtime;

// BOSS-2 (P1 HUSK, docs/boss-encounter-sunderer-design.md): the interposer drone's brain — a deliberately MINIMAL
// IMonsterBehavior that shares nothing with the roam/chase/leash BasicRoamer (no aggro scan, no leash, no attack, no
// pause/roam state machine). Each think-tick it steers toward ONE continuous target supplied by the encounter: the
// midline of the pair's segment (duo) or the boss<->player midpoint (solo). It exists to BODY-BLOCK fusion crossings
// (the B1 contest) — it never harms a player. It moves ONLY through the handed-in locomotion (the "interposer" type
// selects "glide" for a smooth 1.6 u/s walk), so its motion replicates + extrapolates for free with NO protocol
// change; the drone dies to anything through the shared monster-damage path (the tether "melts it"), and the encounter
// engine owns its spawn/respawn cadence + teardown. The midpoint itself is the encounter engine's authority
// (BossEncounterEngine.TryGetInterposeTarget) — this brain stays encounter-agnostic behind the injected delegate.
public sealed class InterposerBehavior : IMonsterBehavior
{
    // Resolve the drone's current interpose target (world point). Returns false when the encounter has nothing
    // sensible to seek (not active / boss gone / no live participant) — the drone then holds position. `drone` is the
    // steering monster (unused today; the encounter tracks its own participants, but it is passed for symmetry with
    // the other behavior seams and in case a future multi-drone variant needs per-drone context).
    public delegate bool TryGetTargetDelegate(WorldEntity drone, out WorldVector target);

    private readonly TryGetTargetDelegate _tryGetTarget;
    private readonly HashSet<ulong> _tracked = [];

    public InterposerBehavior(TryGetTargetDelegate tryGetTarget)
    {
        _tryGetTarget = tryGetTarget ?? throw new ArgumentNullException(nameof(tryGetTarget));
    }

    public int TrackedCount => _tracked.Count;

    // The timing params (pause/aggro cadence) are irrelevant to a drone — it neither pauses nor scans; record the id.
    public void Register(WorldEntity monster, uint serverTick, uint pauseMinTicks, uint pauseMaxTicks, uint aggroScanIntervalTicks)
        => _tracked.Add(monster.Id);

    public void Forget(ulong monsterId) => _tracked.Remove(monsterId);

    // One think-tick: aim at the encounter's interpose target and glide toward it (the locomotion gates the move
    // cadence + faces the heading; the resolver slides/stops it at walls). No target, or already ON it → Stop (a
    // glider parks cleanly). Returns true iff a move committed (diagnostic only, like the other behaviors).
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
