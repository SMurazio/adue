using Mmo.Shared.Domain;

namespace Mmo.Server.Runtime;

// FREEAIM: server-authoritative free-aim melee resolution. Replaces the facing-derived tile cone
// (MeleeConeResolver) with a GEOMETRIC SECTOR: a pie slice of half-angle `halfAngleRadians` and `radiusUnits`,
// centred on the attacker's WORLD position and pointed along the client-chosen continuous `aimRadians`. An entity
// is hit iff (a) its world position is within `radiusUnits` of the attacker AND (b) its bearing from the attacker
// is within the half-angle of the aim. Entities are CIRCLES of EntityHitRadiusTiles (a body) — the wedge hits a
// target it merely CLIPS, not only one whose centre is dead inside it.
//
// World mapping (identical to the client): the entity's CONTINUOUS world Position (X,Y) — fractional, off-grid
// once Phase-1 movement lands — IS the world point fed to the hit test (NOT the rounded tile centre). 1 unit/tile.
// Bearing is atan2(dz, dx) with +X east, +Z south, the SAME convention the client encodes the aim with (both go
// through Mmo.Shared AimAngle), so the angles line up. Feeding the same continuous Position both sides consume is
// the client/server hit-parity contract: the server no longer rounds, so it can no longer miss on the server a hit
// the client predicted by an up-to-0.7-tile rounding gap.
//
// The GameServer attack handler owns only the cursor dedup + the per-entity attack cooldown around this; everything
// "who is in the sector, and who takes damage" lives here so it is unit-testable against a WorldState directly. The
// no-friendly-fire gate (Dummy/Npc/Monster only, never Player, never self) is CombatTargeting.IsAttackableEnemy.
public static class FreeAimSectorResolver
{
    // COMBAT: the target body radius (tiles) for the sector-vs-circle overlap. Now CANONICAL in the shared
    // FreeAimSector (so the client's swing prediction reuses the identical body radius); this forwards to it so the
    // existing server call sites and tests keep reading FreeAimSectorResolver.EntityHitRadiusTiles unchanged.
    public const double EntityHitRadiusTiles = FreeAimSector.EntityHitRadiusTiles;

    // Resolves the free-aim sector for `attacker` aiming along `aimRadians` and applies `damage` to each ENEMY
    // whose CONTINUOUS world Position falls inside the sector (its body circle within radius AND within the
    // half-angle of the aim). Returns the number of entities whose HP actually changed.
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
        double radiusUnits,
        int damage,
        List<WorldEntity> candidateScratch)
        => ResolveAndDamage(world, attacker, aimRadians, halfAngleRadians, radiusUnits, damage, candidateScratch, null);

    // Overload that ALSO appends each victim whose HP actually changed to `damagedScratch` (cleared first when
    // non-null) so the caller can emit a cosmetic damage event per real hit. Behaviour and return value are otherwise
    // identical to the parameterless-collection overload — the existing resolver tests exercise that one unchanged.
    public static int ResolveAndDamage(
        WorldState world,
        WorldEntity attacker,
        double aimRadians,
        double halfAngleRadians,
        double radiusUnits,
        int damage,
        List<WorldEntity> candidateScratch,
        List<DamagedVictim>? damagedScratch)
    {
        damagedScratch?.Clear();

        // Gather every entity within a tile box that is a SUPERSET of the sector's reach, via the SAME spatial index
        // as AOI so occupancy and replication can never diverge; then apply the exact geometric test to each
        // candidate. The box is keyed on the attacker's ROUNDED tile but must cover targets the precise IsHit can
        // still hit, so its radius adds the target BODY radius (a target whose centre is past the sector but whose
        // body clips it is a real hit) AND a +1 tile slack for the attacker's own sub-tile offset from that rounded
        // tile centre (off-grid, Position is up to ~0.5 tile from TileCoord on each axis). Over-gather is free; the
        // precise IsHit filters every spurious candidate. Under-gather would silently DROP a real hit server-side,
        // which the old Ceiling(radiusUnits) — omitting body + offset — could do once attackers move off-grid.
        var gatherRadiusUnits = System.Math.Max(1, (int)System.Math.Ceiling(radiusUnits + EntityHitRadiusTiles) + 1);
        world.GatherInterestCandidates(attacker.TileCoord, gatherRadiusUnits, candidateScratch);

        // Treat each target as a CIRCLE of EntityHitRadiusTiles (a body), not a point: the wedge hits a target it
        // merely CLIPS, not only one whose tile-centre is dead inside it. The geometry (squared range vs radius+body,
        // sqrt + asin(body/dist) angular widen, point-blank always-hit, the NormalizePi reduction) lives in the
        // SHARED Mmo.Shared.Domain.FreeAimSector.IsHit so the client can predict its own swing with the SAME maths.
        var attackerX = attacker.Position.X;
        var attackerZ = attacker.Position.Y;

        var hits = 0;
        foreach (var candidate in candidateScratch)
        {
            if (candidate.Id == attacker.Id || !CombatTargeting.IsAttackableEnemy(candidate))
            {
                continue;
            }

            if (!FreeAimSector.IsHit(
                    attackerX,
                    attackerZ,
                    aimRadians,
                    halfAngleRadians,
                    radiusUnits,
                    EntityHitRadiusTiles,
                    candidate.Position.X,
                    candidate.Position.Y))
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
