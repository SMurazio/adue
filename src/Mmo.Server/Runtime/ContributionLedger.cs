namespace Mmo.Server.Runtime;

// LOOT P4b — the contribution ledger: the group-loot GROUNDWORK laid now even though play is solo. As players
// damage a monster, the ledger records WHO hit it (keyed by the durable player CharacterId — stable across a
// relogin, unlike a reused network id) and HOW MUCH cumulative damage they dealt. On death the set of contributor
// ids becomes the corpse's `eligibleLooters` (solo = just the killer). Damage amounts are kept (cheap) so a later
// loot mode can weight by contribution (top-damage, threshold, etc.) without re-instrumenting the damage path.
//
// Keyed by the monster's ENTITY id (ulong, stable for the monster's life; the network id can be reused on respawn).
// The owner (GameServer) records each damaging hit and, in KillMonster, snapshots + forgets the dead monster's
// ledger entry alongside the existing behavior Forget / _monsterTypeOf.Remove cleanup — so the ledger is cleaned
// up with the monster and never leaks. A monster that is forgotten without dying (despawn for another reason) is
// also cleaned via Forget.
//
// Pure + unit-testable (no world/session/protocol dependency): it traffics only in ulong monster ids and Guid
// contributor ids. The set is a per-monster dictionary, expected to hold a handful of contributors.
public sealed class ContributionLedger
{
    private readonly Dictionary<ulong, Dictionary<Guid, long>> _byMonster = [];

    // Records that the player `contributorId` dealt `damage` to monster `monsterId`. Accumulates the damage into
    // that player's running total for the monster (creating the per-monster + per-player entries on first hit). A
    // non-positive damage still REGISTERS the contributor (they swung and connected — eligibility is "did you
    // participate", and the 0-floor is reachable when a target is already low), so eligibility never depends on the
    // exact damage number. An empty contributor id (a non-durable attacker — should not happen for players) is
    // ignored so the eligible set stays player-only.
    public void RecordDamage(ulong monsterId, Guid contributorId, int damage)
    {
        if (contributorId == Guid.Empty)
        {
            return;
        }

        if (!_byMonster.TryGetValue(monsterId, out var contributors))
        {
            contributors = new Dictionary<Guid, long>();
            _byMonster[monsterId] = contributors;
        }

        var added = damage > 0 ? damage : 0;
        contributors[contributorId] = contributors.GetValueOrDefault(contributorId) + added;
    }

    // The current set of contributor ids for a monster (empty if it was never damaged — e.g. it despawned for a
    // non-combat reason). Returned as a fresh array so the caller can hold it on the corpse independent of the
    // ledger's lifetime (the entry is forgotten right after).
    public IReadOnlyCollection<Guid> Contributors(ulong monsterId)
    {
        if (!_byMonster.TryGetValue(monsterId, out var contributors) || contributors.Count == 0)
        {
            return [];
        }

        return contributors.Keys.ToArray();
    }

    // Cumulative damage a given contributor dealt to a monster (0 if none). Kept for a future contribution-weighted
    // loot mode; unused by FfaAmongEligible.
    public long DamageBy(ulong monsterId, Guid contributorId)
    {
        return _byMonster.TryGetValue(monsterId, out var contributors)
            ? contributors.GetValueOrDefault(contributorId)
            : 0;
    }

    // Drops the monster's ledger entry. Called from KillMonster (after snapshotting the contributors onto the
    // corpse) so the ledger is cleaned up with the monster — and from any other despawn path — so it never leaks.
    // Idempotent: forgetting an unknown monster is a no-op.
    public void Forget(ulong monsterId)
    {
        _byMonster.Remove(monsterId);
    }

    // The number of monsters currently tracked — a leak guard for tests (it must return to 0 once every damaged
    // monster has died/been forgotten).
    public int TrackedMonsterCount => _byMonster.Count;
}
