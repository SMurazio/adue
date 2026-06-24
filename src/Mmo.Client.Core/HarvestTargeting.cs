using Mmo.Shared.Domain;

namespace Mmo.Client.Core;

// Picks which entity a harvest/interact input should target. Adjacency mirrors the server's rule exactly
// (Chebyshev distance <= 1: the actor's tile or any of the 8 surrounding tiles), so the client only ever
// sends an InteractRequest the server can plausibly accept — the server still re-validates authoritatively.
// Targetable = an available resource NODE (never a depleted one) OR a dropped CORPSE (LOOT P4b — the interact
// key loots a corpse you stand next to, through the SAME input + InteractRequest path; the server gates loot
// eligibility). On ties (same Chebyshev distance) we prefer the smaller squared Euclidean distance, then the
// lower NetworkId, so selection is deterministic. Pure/static for unit tests.
public static class HarvestTargeting
{
    // Whether an entity is a valid interact target for the harvest/loot key: an available resource node, or any
    // corpse (corpses are never "depleted" — that bit is resource-only).
    private static bool IsInteractable(in EntityRenderState entity) =>
        entity.Kind == EntityKind.Corpse
        || (entity.Kind == EntityKind.Resource && !entity.Depleted);

    public static bool TryFindNearestHarvestable(
        IReadOnlyList<EntityRenderState> entities,
        TileCoord actorTile,
        out uint targetNetworkId)
    {
        ArgumentNullException.ThrowIfNull(entities);

        var found = false;
        targetNetworkId = 0;
        var bestEuclidean = int.MaxValue;

        foreach (var entity in entities)
        {
            if (!IsInteractable(entity))
            {
                continue;
            }

            var dx = entity.AuthoritativeTile.X - actorTile.X;
            var dy = entity.AuthoritativeTile.Y - actorTile.Y;
            if (Math.Abs(dx) > 1 || Math.Abs(dy) > 1)
            {
                continue;
            }

            var euclidean = (dx * dx) + (dy * dy);
            if (!found
                || euclidean < bestEuclidean
                || (euclidean == bestEuclidean && entity.NetworkId < targetNetworkId))
            {
                found = true;
                bestEuclidean = euclidean;
                targetNetworkId = entity.NetworkId;
            }
        }

        return found;
    }
}
