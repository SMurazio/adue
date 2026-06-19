using Mmo.Shared.Domain;

namespace Mmo.Client.Core;

// Picks which resource node a harvest input should target. Adjacency mirrors the server's rule exactly
// (Chebyshev distance <= 1: the actor's tile or any of the 8 surrounding tiles), so the client only ever
// sends an InteractRequest the server can plausibly accept — the server still re-validates authoritatively.
// We never target a depleted node. On ties (same Chebyshev distance) we prefer the smaller squared
// Euclidean distance, then the lower NetworkId, so selection is deterministic. Pure/static for unit tests.
public static class HarvestTargeting
{
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
            if (entity.Kind != EntityKind.Resource || entity.Depleted)
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
