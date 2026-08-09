using Mmo.Shared.Domain;

namespace Mmo.Server.Runtime;

// ADUE P2-A (todo/S-p2-practice-room-and-dummy.md): the PRACTICE DUMMY's brain — the deliberately MINIMAL
// IMonsterBehavior (the Interposer/Splinter sibling; it shares their "record the id, harmless, no aggro/leash/attack"
// shape). Unlike those two it seeks NOTHING: each think-tick it just parks (locomotion.Stop) and returns — the dummy
// stands exactly on its DummySpawnTile forever, a fixed target the pair rehearses the four duo verbs against.
//
// WHY A DEDICATED BRAIN, not basicRoamer with aggroRadius 0: the MonsterTypeRegistry CLAMPS aggroRadius to a 0.5-unit
// floor, so a basicRoamer dummy WOULD aggro (and, with a non-zero attackDamage, attack) a player standing within 0.5u —
// exactly the point-blank case the practice room puts the pair in. This brain ignores aggro/attack ENTIRELY (it never
// invokes the aggro-scan or attack seams), so the non-aggression guarantee is robust regardless of the aggro clamp. It
// also never drives the locomotion toward a target, so the dummy never drifts off its spawn tile (deterministic for
// tests + a stable rehearsal target). A dedicated class matches the codebase's one-class-per-brain pattern (BasicRoamer /
// Skirmisher / Interposer / Splinter); a shared "does-nothing" base is not worth it for a single trivial brain.
public sealed class StationaryBehavior : IMonsterBehavior
{
    private readonly HashSet<ulong> _tracked = [];

    // Test/diagnostic visibility, mirroring the other minimal brains.
    public int TrackedCount => _tracked.Count;

    // The timing params (pause/aggro cadence) are irrelevant to a dummy — it neither pauses nor scans; just record the id.
    public void Register(WorldEntity monster, uint serverTick, uint pauseMinTicks, uint pauseMaxTicks, uint aggroScanIntervalTicks)
        => _tracked.Add(monster.Id);

    public void Forget(ulong monsterId) => _tracked.Remove(monsterId);

    // One think-tick: park it (Stop zeroes velocity so a glider extrapolates nowhere) and do nothing else — no aggro scan,
    // no chase, no attack, no move. Always returns false (never committed a move — diagnostic only, like the siblings).
    public bool StepMonster(
        WorldEntity monster, uint serverTick, uint cooldownTicks, in MonsterAiTunables tunables, IMonsterLocomotion locomotion)
    {
        if (_tracked.Contains(monster.Id))
        {
            locomotion.Stop(monster);
        }

        return false;
    }
}
