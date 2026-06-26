namespace Mmo.Shared.Domain;

// CONTINUOUS MIGRATION (Phase 9): the SHARED interaction-reach constant both the SERVER's interact gate
// (GameServer.HandleInteract — harvest a node / open + loot a corpse) AND the CLIENT's harvest targeting
// (HarvestTargeting — which node/corpse is "in reach / highlighted") read, so the two can never drift. The
// server re-validates authoritatively; the client mirrors the identical radius so the player only ever sees
// harvestable exactly what the server will accept.
//
// WHY 1.5 tiles: this floats the former tile Chebyshev <= 1 adjacency (a 3x3 tile box) to a Euclidean distance
// on the CONTINUOUS positions, the same int->float pattern as Phase 6 (AOI) and Phase 7 (combat). Chebyshev <= 1
// reaches the actor's tile and the 8 surrounding tiles — up to ~1.414 tiles away on a diagonal (sqrt(1^2 + 1^2)).
// A radius of 1.5 tiles fully covers that diagonal reach (1.414 < 1.5) so harvesting is never SHORTER than today,
// and is only marginally (≈0.09 tile) more generous on the diagonal — the natural "preserve the reach" value once
// the box becomes a circle. Both the actor's and the target's CONTINUOUS world positions feed the squared-distance
// gate (resources/corpses are still authored on tile centres, so their Position is the tile centre).
public static class InteractionTuning
{
    // Maximum Euclidean distance (in tile units, 1.0 == one tile) between the actor's continuous position and the
    // target's continuous position for an interact (harvest / loot-open) to be in reach. See the type comment for
    // why 1.5 preserves the old Chebyshev <= 1 reach.
    public const double InteractionRadiusTiles = 1.5d;

    // The radius squared — compare against (actor.Position - target.Position).LengthSquared to avoid a sqrt on the
    // hot path. Both the server gate and the client targeting use THIS so they cannot diverge.
    public const double InteractionRadiusTilesSquared = InteractionRadiusTiles * InteractionRadiusTiles;
}
