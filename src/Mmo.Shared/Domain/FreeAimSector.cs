namespace Mmo.Shared.Domain;

// FREEAIM (shared): the pure per-target hit test for the free-aim melee sector — a pie slice of half-angle
// `halfAngleRadians` and `radiusUnits`, centred on the attacker's WORLD position and pointed along the continuous
// `aimRadians`. A target is a CIRCLE of `bodyRadiusUnits` (a body), so the wedge hits a target it merely CLIPS,
// not only one whose centre is dead inside it.
//
// This is the SINGLE source of the geometry. The server's FreeAimSectorResolver gathers AOI candidates, applies
// the friendly-fire gate, calls THIS per candidate, then ApplyDamage — i.e. authoritative behaviour is unchanged,
// the maths just moved here. The Godot client calls THIS against its rendered enemy positions to PREDICT (cosmetic
// only) a damage number on swing, so the predicted hit/miss matches the server's resolution exactly.
//
// World mapping (identical on both sides): TileCoord (X,Y) -> world (X, 0, Y), 1 unit/tile. Bearing is
// atan2(dz, dx) with +X east, +Z south — the same convention AimAngle encodes the aim with, so the angles line up.
public static class FreeAimSector
{
    // COMBAT: the target body radius (tiles) for the sector-vs-circle overlap. Targets are circles of this radius,
    // so the wedge hits one it merely CLIPS, not only one whose centre is inside it. ~half a tile = forgiving but
    // not oversized. Lives here (shared) so BOTH the server resolver and the client's swing prediction use the
    // identical body radius. (The server reads it via FreeAimSectorResolver.EntityHitRadiusTiles, which forwards
    // here; the Godot client reads it directly.)
    public const double EntityHitRadiusTiles = 0.5;

    // True iff a target at (targetX, targetZ) — a body circle of `bodyRadiusUnits` — overlaps the sector centred on
    // the attacker at (attackerX, attackerZ), reaching `radiusUnits` along `aimRadians` within ±`halfAngleRadians`.
    //
    // Geometry is byte-for-byte the resolver's: squared range gate vs (radius + body), then for a non-overlapping
    // target the angular gate widened by the body's angular half-width asin(body/dist); a target overlapping the
    // attacker (dist <= body) is always in-sector (point-blank always-hit).
    public static bool IsHit(
        double attackerX,
        double attackerZ,
        double aimRadians,
        double halfAngleRadians,
        double radiusUnits,
        double bodyRadiusUnits,
        double targetX,
        double targetZ)
    {
        var dx = targetX - attackerX;
        var dz = targetZ - attackerZ;

        // Range gate (squared, cheap): the target's body circle must reach within the sector radius.
        var reach = radiusUnits + bodyRadiusUnits;
        var distSquared = (dx * dx) + (dz * dz);
        if (distSquared > reach * reach)
        {
            return false;
        }

        var dist = System.Math.Sqrt(distSquared);
        if (dist <= bodyRadiusUnits)
        {
            // Overlapping the attacker — always in-sector (point-blank always-hit), no angular gate.
            return true;
        }

        // Angular gate widened by the body's angular half-width asin(body/dist) so a target the wedge edge clips
        // still counts.
        var bearing = System.Math.Atan2(dz, dx);
        var delta = NormalizePi(bearing - aimRadians);
        var angularHalfWidth = System.Math.Asin(System.Math.Min(bodyRadiusUnits / dist, 1d));
        return System.Math.Abs(delta) <= halfAngleRadians + angularHalfWidth;
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
