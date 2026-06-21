using Mmo.Shared.Domain;

namespace Mmo.Server.Runtime;

// COMBAT-S2B: server-authoritative melee-cone resolution — the cone tiles (rotated by the attacker's facing),
// the spatial occupancy query, the no-friendly-fire gate, and the damage application, in one testable place. The
// GameServer attack handler owns only the cursor dedup + the per-entity attack cooldown around this; everything
// "who is on the cone, and who takes damage" lives here so it can be unit-tested against a WorldState directly
// (no live network / GameServer plumbing needed).
public static class MeleeConeResolver
{
    // Computes the melee cone in front of `attacker` (MeleeCone.Resolve, rotated by facing), queries occupancy on
    // those tiles via the spatial grid, and applies `damage` to each ENEMY standing on a cone tile. Returns the
    // number of entities whose HP actually changed.
    //
    // No friendly fire: only Dummy/Npc kinds are damaged — never another Player, never the attacker itself, never
    // non-combatants (resources / stat-less entities, which ApplyDamage no-ops on anyway). The reduced HP rides the
    // existing public-HP snapshot field, so the target's overhead bar drops automatically (no dedicated reply).
    // HP may reach 0 (no death/despawn this stage).
    //
    // `candidateScratch` is a caller-owned reusable buffer (cleared by the gather) so the hot path allocates
    // nothing per attack.
    public static int ResolveAndDamage(
        WorldState world,
        WorldEntity attacker,
        int damage,
        List<WorldEntity> candidateScratch)
    {
        Span<TileCoord> coneTiles = stackalloc TileCoord[MeleeCone.TileCount];
        MeleeCone.Resolve(attacker.Tile, attacker.Facing, coneTiles);

        // The cone is entirely within 1 tile of the attacker, so a radius-1 neighborhood gather is a superset of
        // every entity that could be on a cone tile. Route it through the SAME spatial index as AOI so occupancy
        // and replication can never diverge; then filter to the exact cone tiles.
        world.GatherInterestCandidates(attacker.Tile, 1, candidateScratch);

        var hits = 0;
        foreach (var candidate in candidateScratch)
        {
            if (candidate.Id == attacker.Id || !IsAttackableEnemy(candidate))
            {
                continue;
            }

            if (!ConeContains(coneTiles, candidate.Tile))
            {
                continue;
            }

            if (candidate.ApplyDamage(damage))
            {
                hits++;
            }
        }

        return hits;
    }

    // An attack damages enemies only — the target dummy and NPCs. Other Players are never damaged (no friendly fire
    // this stage); resource nodes / stat-less entities are not combatants. The single friendly-fire gate, so a
    // future PvP toggle changes only here.
    public static bool IsAttackableEnemy(WorldEntity entity)
    {
        return entity.Kind is EntityKind.Dummy or EntityKind.Npc;
    }

    // True iff `tile` is one of the resolved cone tiles. Linear over a fixed 3-element span — cheaper than a set
    // for so few tiles, and allocation-free.
    private static bool ConeContains(ReadOnlySpan<TileCoord> coneTiles, TileCoord tile)
    {
        foreach (var coneTile in coneTiles)
        {
            if (coneTile == tile)
            {
                return true;
            }
        }

        return false;
    }
}
