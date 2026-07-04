using Mmo.Shared.Domain;

namespace Mmo.Server.Runtime;

// DUO-WAVE2 ability 3 (Laser Tether): the PURE band + damage-scaling geometry of the tether beam — no state, no world,
// so it is directly unit-testable (the band-boundary + orbit-sweep tests). Distances are tile units; the beam is the
// segment between the two paired players. Kept beside TetherEngine (server-side) — server-authoritative resolution the
// client never reproduces (the client only colours the beam by the same band it recomputes from the two positions).
public enum TetherBand
{
    Inert = 0,       // below the minimum range — the beam is slack, does nothing
    Sweet = 1,       // the sweet-spot band — the beam damages enemies it crosses
    Warning = 2,     // past sweet but not yet overstretched — a no-damage tension warning zone
    Overstretch = 3, // beyond the max range — the beam over-tensions, hurting BOTH players, then breaks
}

public static class TetherMath
{
    // Classify the current beam length into a band. Bounds (from the orchestrator spec): inert < inertMax; sweet in
    // [inertMax, sweetMax]; warning in (sweetMax, overstretchMin); overstretch >= overstretchMin. The (sweetMax,
    // overstretchMin) gap is the deliberate WARNING zone — no damage either way. Inclusive on the sweet band's edges
    // (a length exactly on inertMax or sweetMax is in-band), so the drawn/felt edge is the rule.
    public static TetherBand Band(double distanceUnits, double inertMax, double sweetMax, double overstretchMin)
    {
        if (distanceUnits < inertMax)
        {
            return TetherBand.Inert;
        }

        if (distanceUnits <= sweetMax)
        {
            return TetherBand.Sweet;
        }

        if (distanceUnits < overstretchMin)
        {
            return TetherBand.Warning;
        }

        return TetherBand.Overstretch;
    }

    // The per-damage-tick damage a sweet-spot beam of length `distanceUnits` deals — scaling toward the MIDDLE of the
    // band (max at `midUnits`, min at the band edges). frac is the normalized distance from the middle (0 at the
    // middle, 1 at whichever edge is farther), so damage = round(max - frac*(max-min)), clamped to [min, max]. A
    // length outside the sweet band clamps to the min (defensive — the caller only calls this in-band).
    public static int SweetTickDamage(
        double distanceUnits, double sweetMin, double sweetMax, double midUnits, int minDamage, int maxDamage)
    {
        var halfSpan = System.Math.Max(midUnits - sweetMin, sweetMax - midUnits);
        if (halfSpan <= 0d)
        {
            return maxDamage;
        }

        var frac = System.Math.Abs(distanceUnits - midUnits) / halfSpan;
        frac = System.Math.Clamp(frac, 0d, 1d);
        var scaled = maxDamage - (frac * (maxDamage - minDamage));
        var rounded = (int)System.Math.Round(scaled, System.MidpointRounding.AwayFromZero);
        return System.Math.Clamp(rounded, minDamage, maxDamage);
    }

    // True iff a monster body (centred at `monsterPosition`, radius `hitRadiusUnits`) overlaps the beam segment
    // [a,b]. Reuses the pure point-segment distance the skillshot flight uses so a monster the beam grazes is hit,
    // not only a dead-centre crossing — exactly what makes SWEEPING the beam through a cluster feel good.
    public static bool SegmentHitsBody(
        WorldVector monsterPosition, WorldVector a, WorldVector b, double hitRadiusUnits)
    {
        var (distance, _) = SkillshotMath.PointSegmentDistance(monsterPosition, a, b);
        return distance <= hitRadiusUnits;
    }
}
