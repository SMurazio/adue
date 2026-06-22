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
    // COMBAT: the target body radius (tiles) for the sector-vs-circle overlap. Now CANONICAL in the shared
    // FreeAimSector (so the client's swing prediction reuses the identical body radius); this forwards to it so the
    // existing server call sites and tests keep reading FreeAimSectorResolver.EntityHitRadiusTiles unchanged.
    public const double EntityHitRadiusTiles = FreeAimSector.EntityHitRadiusTiles;

    // Resolves the free-aim sector for `attacker` aiming along `aimRadians` and applies `damage` to each ENEMY
    // whose tile-centre world position falls inside the sector (within radius AND within ±halfAngle of the aim).
    // Returns the number of entities whose HP actually changed.
    //
    // `candidateScratch` is a caller-owned reusable buffer (cleared by the gather) so the hot path allocates
    // nothing per attack — mirrors the cone resolver's contract.
    // A single victim that actually took damage from a resolved attack: the entity and the HP actually removed this
    // hit (equal to `damage` for now, but kept explicit so a future variable/partial damage still reports correctly).
    // COMBAT-QOL: HandleAttack turns each of these into an AOI-gated cosmetic DamageEventMessage.
    public readonly record struct DamagedVictim(WorldEntity Victim, int Amount);

    public static int ResolveAndDamage(
        WorldState world,
        WorldEntity attacker,
        double aimRadians,
        double halfAngleRadians,
        double radiusTiles,
        int damage,
        List<WorldEntity> candidateScratch)
        => ResolveAndDamage(world, attacker, aimRadians, halfAngleRadians, radiusTiles, damage, candidateScratch, null);

    // Overload that ALSO appends each victim whose HP actually changed to `damagedScratch` (cleared first when
    // non-null) so the caller can emit a cosmetic damage event per real hit. Behaviour and return value are otherwise
    // identical to the parameterless-collection overload — the existing resolver tests exercise that one unchanged.
    public static int ResolveAndDamage(
        WorldState world,
        WorldEntity attacker,
        double aimRadians,
        double halfAngleRadians,
        double radiusTiles,
        int damage,
        List<WorldEntity> candidateScratch,
        List<DamagedVictim>? damagedScratch)
    {
        damagedScratch?.Clear();

        // Gather every entity within a tile box that is a SUPERSET of the sector's reach (radius rounded up), via
        // the SAME spatial index as AOI so occupancy and replication can never diverge; then apply the exact
        // geometric test to each candidate.
        var gatherRadiusTiles = System.Math.Max(1, (int)System.Math.Ceiling(radiusTiles));
        world.GatherInterestCandidates(attacker.Tile, gatherRadiusTiles, candidateScratch);

        // Treat each target as a CIRCLE of EntityHitRadiusTiles (a body), not a point: the wedge hits a target it
        // merely CLIPS, not only one whose tile-centre is dead inside it. The geometry (squared range vs radius+body,
        // sqrt + asin(body/dist) angular widen, point-blank always-hit, the NormalizePi reduction) lives in the
        // SHARED Mmo.Shared.Domain.FreeAimSector.IsHit so the client can predict its own swing with the SAME maths.
        var attackerX = (double)attacker.Tile.X;
        var attackerZ = (double)attacker.Tile.Y;

        var hits = 0;
        foreach (var candidate in candidateScratch)
        {
            if (candidate.Id == attacker.Id || !MeleeConeResolver.IsAttackableEnemy(candidate))
            {
                continue;
            }

            if (!FreeAimSector.IsHit(
                    attackerX,
                    attackerZ,
                    aimRadians,
                    halfAngleRadians,
                    radiusTiles,
                    EntityHitRadiusTiles,
                    candidate.Tile.X,
                    candidate.Tile.Y))
            {
                continue;
            }

            if (candidate.ApplyDamage(damage))
            {
                hits++;
                damagedScratch?.Add(new DamagedVictim(candidate, damage));
            }
        }

        return hits;
    }
}
