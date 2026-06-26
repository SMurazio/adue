using Mmo.Shared.Domain;

namespace Mmo.Client.Core;

// Picks which entity a harvest/interact input should target. Reach mirrors the server's rule exactly
// (CONTINUOUS MIGRATION Phase 9: Euclidean distance <= InteractionTuning.InteractionRadiusTiles on the
// CONTINUOUS positions, the same shared constant the server's interact gate reads — was tile Chebyshev <= 1),
// so the client only ever sends an InteractRequest the server can plausibly accept — the server still
// re-validates authoritatively. Targetable = an available resource NODE (never a depleted one) OR a dropped
// CORPSE (LOOT P4b — the interact key loots a corpse you stand next to, through the SAME input + InteractRequest
// path; the server gates loot eligibility). On ties (same squared distance) we prefer the lower NetworkId, so
// selection is deterministic. Pure/static for unit tests.
//
// S53 / Phase 9 parity: the actor position fed here is the SERVER-CONFIRMED continuous position (MmoClient's
// confirmed WorldVector), NOT the predicted/interpolated render position — targeting must read confirmed state.
// The targets (resource node / corpse) are authored on tile centres, so their confirmed continuous position is
// the tile centre (WorldVector.FromTile(AuthoritativeTile)); feeding both continuous positions into the SAME
// shared squared-radius gate is the client/server reach-parity contract.
public static class HarvestTargeting
{
    // Whether an entity is a valid interact target for the harvest/loot key: an available resource node, or any
    // corpse (corpses are never "depleted" — that bit is resource-only).
    private static bool IsInteractable(in EntityRenderState entity) =>
        entity.Kind == EntityKind.Corpse
        || (entity.Kind == EntityKind.Resource && !entity.Depleted);

    public static bool TryFindNearestHarvestable(
        IReadOnlyList<EntityRenderState> entities,
        WorldVector actorPosition,
        out uint targetNetworkId)
    {
        ArgumentNullException.ThrowIfNull(entities);

        var found = false;
        targetNetworkId = 0;
        var bestDistanceSquared = double.MaxValue;

        foreach (var entity in entities)
        {
            if (!IsInteractable(entity))
            {
                continue;
            }

            // The target's CONFIRMED continuous position: resources/corpses are tile-placed, so the authoritative
            // tile centre IS that position. Euclidean squared distance against the SAME shared radius the server
            // gates with — so an in-range verdict here matches the server's accept exactly.
            var targetPosition = WorldVector.FromTile(entity.AuthoritativeTile);
            var distanceSquared = (actorPosition - targetPosition).LengthSquared;
            if (distanceSquared > InteractionTuning.InteractionRadiusTilesSquared)
            {
                continue;
            }

            if (!found
                || distanceSquared < bestDistanceSquared
                || (distanceSquared == bestDistanceSquared && entity.NetworkId < targetNetworkId))
            {
                found = true;
                bestDistanceSquared = distanceSquared;
                targetNetworkId = entity.NetworkId;
            }
        }

        return found;
    }
}
