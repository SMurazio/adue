using Mmo.Client.Core.Population;
using Mmo.Shared.Domain;

namespace Mmo.Client.Core;

// NODE-FIELD N3 (docs/node-field-design.md D5/D6): picks which CATALOGUE node an E-press harvest input should
// target — the node-field analogue of HarvestTargeting.TryFindNearestCorpse (which used to also cover resource
// nodes, before N3), now over indices instead of entities (harvestable nodes are no longer WorldEntities;
// HarvestTargeting itself was trimmed to corpses-only in lockstep with this, see its own updated comment).
// Reach mirrors the server's rule exactly (Euclidean
// distance <= InteractionTuning.InteractionRadiusUnits — the SAME shared constant HandleHarvestNode's
// IsWithinNodeInteractionRange reads), so the client only ever sends a HarvestNodeMessage the server can
// plausibly accept — the server still re-validates authoritatively (index/availability/range) itself.
//
// Only scans the actor's chunk + its 8 neighbours via NodeFieldChunkIndex (never the whole ~5,000-entry
// catalogue) — see that type's own comment for why the 3x3 neighbourhood is always sufficient at this reach.
// On ties (same squared distance) prefers the lower catalogue Index, mirroring HarvestTargeting's
// lower-NetworkId tie-break, so selection is deterministic.
public static class NodeFieldTargeting
{
    public static bool TryFindNearestAvailableNode(
        NodeFieldChunkIndex chunkIndex,
        IReadOnlySet<ushort> depletedIndices,
        WorldVector actorPosition,
        out ushort nodeIndex,
        out double distanceSquared)
    {
        ArgumentNullException.ThrowIfNull(chunkIndex);
        ArgumentNullException.ThrowIfNull(depletedIndices);

        var found = false;
        nodeIndex = 0;
        distanceSquared = double.MaxValue;
        var bestDistanceSquared = double.MaxValue;

        var actorTile = actorPosition.ToTileRounded();
        var (actorCx, actorCz) = NodeFieldChunkIndex.ChunkOf(actorTile);

        for (var dz = -1; dz <= 1; dz++)
        {
            for (var dx = -1; dx <= 1; dx++)
            {
                var candidates = chunkIndex.EntriesIn((actorCx + dx, actorCz + dz));
                if (candidates.Count == 0)
                {
                    continue;
                }

                foreach (var entry in candidates)
                {
                    // depletedIndices is the client's mirror of server-broadcast state; every member here came
                    // straight from this SAME chunk index's own entries (entry.Index), so no bounds check is
                    // needed on THIS side — the unchecked direction is resolving an arbitrary wire index back
                    // to a chunk (NodeFieldChunkIndex.TryChunkOfIndex), not this membership test.
                    if (depletedIndices.Contains((ushort)entry.Index))
                    {
                        continue;
                    }

                    var targetPosition = WorldVector.FromTile(entry.Tile);
                    var candidateDistanceSquared = (actorPosition - targetPosition).LengthSquared;
                    if (candidateDistanceSquared > InteractionTuning.InteractionRadiusUnitsSquared)
                    {
                        continue;
                    }

                    if (!found
                        || candidateDistanceSquared < bestDistanceSquared
                        || (candidateDistanceSquared == bestDistanceSquared && entry.Index < nodeIndex))
                    {
                        found = true;
                        bestDistanceSquared = candidateDistanceSquared;
                        nodeIndex = (ushort)entry.Index;
                    }
                }
            }
        }

        distanceSquared = bestDistanceSquared;
        return found;
    }
}
