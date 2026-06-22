using Mmo.Shared.Domain;

namespace Mmo.Server.Runtime;

// FREEAIM: server-authoritative free-aim melee resolution. Replaces the facing-derived tile cone
// (MeleeConeResolver) with a GEOMETRIC SECTOR: a pie slice of half-angle `halfAngleRadians` and `radiusTiles`,
// centred on the attacker's WORLD position and pointed along the client-chosen continuous `aimRadians`. An entity
// is hit iff (a) its world position is within `radiusTiles` of the attacker AND (b) its bearing from the attacker
// is within ±halfAngle of the aim. Entities are treated as POINTS at their tile-centre world position for now
// (no body radius) — flag for tuning if melee feels like it "just misses" adjacent targets.
//
// World mapping (identical to the client): TileCoord (X,Y) -> world (X, 0, Y), 1 unit/tile, so a tile-centre is
// simply (tile.X, tile.Y) in the XZ plane. Bearing is atan2(dz, dx) with +X east, +Z south — the SAME convention
// the client encodes the aim with (both go through Mmo.Shared AimAngle), so the angles line up.
//
// As with MeleeConeResolver, the GameServer attack handler owns only the cursor dedup + the per-entity attack
// cooldown around this; everything "who is in the sector, and who takes damage" lives here so it is unit-testable
// against a WorldState directly. The no-friendly-fire gate (Dummy/Npc only, never Player, never self) is reused
// verbatim from MeleeConeResolver.IsAttackableEnemy.
public static class FreeAimSectorResolver
{
    // Resolves the free-aim sector for `attacker` aiming along `aimRadians` and applies `damage` to each ENEMY
    // whose tile-centre world position falls inside the sector (within radius AND within ±halfAngle of the aim).
    // Returns the number of entities whose HP actually changed.
    //
    // `candidateScratch` is a caller-owned reusable buffer (cleared by the gather) so the hot path allocates
    // nothing per attack — mirrors the cone resolver's contract.
    public static int ResolveAndDamage(
        WorldState world,
        WorldEntity attacker,
        double aimRadians,
        double halfAngleRadians,
        double radiusTiles,
        int damage,
        List<WorldEntity> candidateScratch)
    {
        // Gather every entity within a tile box that is a SUPERSET of the sector's reach (radius rounded up), via
        // the SAME spatial index as AOI so occupancy and replication can never diverge; then apply the exact
        // geometric test to each candidate.
        var gatherRadiusTiles = System.Math.Max(1, (int)System.Math.Ceiling(radiusTiles));
        world.GatherInterestCandidates(attacker.Tile, gatherRadiusTiles, candidateScratch);

        var radiusSquared = radiusTiles * radiusTiles;
        var attackerX = (double)attacker.Tile.X;
        var attackerZ = (double)attacker.Tile.Y;

        var hits = 0;
        foreach (var candidate in candidateScratch)
        {
            if (candidate.Id == attacker.Id || !MeleeConeResolver.IsAttackableEnemy(candidate))
            {
                continue;
            }

            var dx = candidate.Tile.X - attackerX;
            var dz = candidate.Tile.Y - attackerZ;

            // (b for free first) Radius gate. A point exactly on the attacker's tile (distance 0) has no defined
            // bearing; treat it as in-sector (it is point-blank and within any radius) and let it through.
            var distSquared = (dx * dx) + (dz * dz);
            if (distSquared > radiusSquared)
            {
                continue;
            }

            if (distSquared > 0d)
            {
                // (b) Angular gate: bearing of the candidate from the attacker, reduced against the aim to (-π, π].
                var bearing = System.Math.Atan2(dz, dx);
                var delta = NormalizePi(bearing - aimRadians);
                if (System.Math.Abs(delta) > halfAngleRadians)
                {
                    continue;
                }
            }

            if (candidate.ApplyDamage(damage))
            {
                hits++;
            }
        }

        return hits;
    }

    // Reduce an angle difference to the principal range (-π, π] so the |delta| <= halfAngle test is correct across
    // the 0/2π seam (e.g. aim just below 2π, target just above 0).
    private static double NormalizePi(double radians)
    {
        var twoPi = 2d * System.Math.PI;
        radians %= twoPi;
        if (radians <= -System.Math.PI)
        {
            radians += twoPi;
        }
        else if (radians > System.Math.PI)
        {
            radians -= twoPi;
        }

        return radians;
    }
}
