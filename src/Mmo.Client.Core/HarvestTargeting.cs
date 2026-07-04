using Mmo.Shared.Domain;

namespace Mmo.Client.Core;

// Picks which CORPSE an interact input should loot. Reach mirrors the server's rule exactly (CONTINUOUS
// MIGRATION Phase 9: Euclidean distance <= InteractionTuning.InteractionRadiusUnits on the CONTINUOUS
// positions, the same shared constant the server's interact gate reads), so the client only ever sends an
// InteractRequest the server can plausibly accept — the server still re-validates authoritatively. On ties
// (same squared distance) we prefer the lower NetworkId, so selection is deterministic. Pure/static for unit
// tests.
//
// NODE-FIELD N3 (docs/node-field-design.md D5/D6): harvestable resource nodes are no longer WorldEntities — a
// node is never in `entities` at all now, so the old `entity.Kind == EntityKind.Resource && !Depleted` branch
// this class used to also match is GONE (it would only ever have matched a House/Portal prop, which was never
// actually harvestable — HandleInteract's "not_resource" reply for anything but a corpse confirms that). Node
// harvest targeting is the SEPARATE catalogue-indexed NodeFieldTargeting.TryFindNearestAvailableNode;
// MmoClientRoot.TryHarvest calls BOTH and sends whichever is nearer.
//
// S53 / Phase 9 parity: the actor position fed here is the SERVER-CONFIRMED continuous position (MmoClient's
// confirmed WorldVector), NOT the predicted/interpolated render position — targeting must read confirmed state.
// A corpse is authored on a tile centre, so its confirmed continuous position is the tile centre
// (WorldVector.FromTile(AuthoritativeTile)); feeding both continuous positions into the SAME shared
// squared-radius gate is the client/server reach-parity contract.
public static class HarvestTargeting
{
    public static bool TryFindNearestCorpse(
        IReadOnlyList<EntityRenderState> entities,
        WorldVector actorPosition,
        out uint targetNetworkId,
        out double distanceSquared)
    {
        ArgumentNullException.ThrowIfNull(entities);

        var found = false;
        targetNetworkId = 0;
        distanceSquared = double.MaxValue;
        var bestDistanceSquared = double.MaxValue;

        foreach (var entity in entities)
        {
            if (entity.Kind != EntityKind.Corpse)
            {
                continue;
            }

            // The target's CONFIRMED continuous position: a corpse is tile-placed, so the authoritative tile
            // centre IS that position. Euclidean squared distance against the SAME shared radius the server
            // gates with — so an in-range verdict here matches the server's accept exactly.
            var targetPosition = WorldVector.FromTile(entity.AuthoritativeTile);
            var candidateDistanceSquared = (actorPosition - targetPosition).LengthSquared;
            if (candidateDistanceSquared > InteractionTuning.InteractionRadiusUnitsSquared)
            {
                continue;
            }

            if (!found
                || candidateDistanceSquared < bestDistanceSquared
                || (candidateDistanceSquared == bestDistanceSquared && entity.NetworkId < targetNetworkId))
            {
                found = true;
                bestDistanceSquared = candidateDistanceSquared;
                targetNetworkId = entity.NetworkId;
            }
        }

        distanceSquared = bestDistanceSquared;
        return found;
    }
}
