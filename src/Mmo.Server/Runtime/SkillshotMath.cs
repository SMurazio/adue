using Mmo.Shared.Domain;

namespace Mmo.Server.Runtime;

// DUO-SKILLSHOT (exp/duo-abilities): the PURE geometry of the fusion skillshot — no state, no world, no seams, so it
// is directly unit-testable (the "fusion window math / bisector / flight" tests). Kept beside the engine (server-side)
// rather than in Mmo.Shared because it is server-authoritative resolution the client never reproduces (unlike the
// movement integrator). All lengths are tile units; time is seconds; dt is the fixed server tick (1/TickRate).
public static class SkillshotMath
{
    // The bearing→unit-heading and the straight-line step are trivial, but named so the engine reads declaratively and
    // the flight test targets exactly this rule.
    public static WorldVector Advance(WorldVector position, WorldVector unitDir, double speed, double dtSeconds)
        => position + (unitDir * (speed * dtSeconds));

    // The fused travel direction = the normalized BISECTOR of the two shots' unit headings (design: "averaged
    // trajectory (bisector of the two travel directions)"). A degenerate case — the two headings are exactly opposite,
    // so their sum is ~zero and has no direction — falls back to `unitDirA` (any consistent choice; the paths were
    // head-on, so either original heading is a reasonable merge). Inputs are assumed unit-length (the engine stores
    // normalized directions); this normalizes the sum defensively regardless.
    public static WorldVector Bisector(WorldVector unitDirA, WorldVector unitDirB)
    {
        var sum = unitDirA + unitDirB;
        return sum.LengthSquared > 1e-9d ? sum.Normalized() : unitDirA.Normalized();
    }

    // The classification of a candidate fusion between two in-flight projectiles, evaluated on THIS tick's look-ahead
    // segments. Fused is false when the paths don't cross in-window; when true, Tier is Good or Perfect and
    // CrossingPoint is the merge origin (the midpoint of the closest approach on the window that classified).
    public readonly record struct FusionEvaluation(bool Fused, ProjectileTier Tier, WorldVector CrossingPoint);

    // Evaluate whether two projectiles fuse this tick. Each projectile sweeps a SEGMENT over the fusion WINDOW (N ticks
    // of its own velocity) — the "within the same N-tick window" temporal gate is literally the segment length, so a
    // near-miss whose crossing is still several ticks away is not fused yet (it re-tests next tick, when the crossing
    // has drawn closer). Perfect is the tight test (small distance + short window); Good is the loose test. Perfect is
    // checked first so a pair that qualifies for both is the stronger tier. The distance is the closest approach
    // between the two window segments (paths crossing => distance 0).
    public static FusionEvaluation EvaluateFusion(
        WorldVector posA, WorldVector unitDirA, double speedA,
        WorldVector posB, WorldVector unitDirB, double speedB,
        double dtSeconds,
        double perfectDistance, int perfectWindowTicks,
        double goodDistance, int goodWindowTicks)
    {
        var velA = unitDirA * speedA;
        var velB = unitDirB * speedB;

        var (perfectGap, perfectMid) = SegmentClosestApproach(
            posA, posA + (velA * (dtSeconds * perfectWindowTicks)),
            posB, posB + (velB * (dtSeconds * perfectWindowTicks)));
        if (perfectGap <= perfectDistance)
        {
            return new FusionEvaluation(true, ProjectileTier.Perfect, perfectMid);
        }

        var (goodGap, goodMid) = SegmentClosestApproach(
            posA, posA + (velA * (dtSeconds * goodWindowTicks)),
            posB, posB + (velB * (dtSeconds * goodWindowTicks)));
        if (goodGap <= goodDistance)
        {
            return new FusionEvaluation(true, ProjectileTier.Good, goodMid);
        }

        return new FusionEvaluation(false, ProjectileTier.Solo, WorldVector.Zero);
    }

    // Closest approach between two segments [a0,a1] and [b0,b1]: returns the minimum distance and the MIDPOINT of the
    // two closest points (the geometric "crossing" point). Standard clamped segment/segment solve (Ericson, Real-Time
    // Collision Detection): parameterize each segment, minimize the squared gap, clamp both params to [0,1], with the
    // degenerate zero-length cases handled so a stationary/zero-window segment never divides by zero.
    public static (double Distance, WorldVector Midpoint) SegmentClosestApproach(
        WorldVector a0, WorldVector a1, WorldVector b0, WorldVector b1)
    {
        var d1 = a1 - a0; // direction+length of segment A
        var d2 = b1 - b0; // direction+length of segment B
        var r = a0 - b0;
        var a = d1.LengthSquared;
        var e = d2.LengthSquared;
        var f = d2.Dot(r);

        double s;
        double t;
        const double eps = 1e-12d;

        if (a <= eps && e <= eps)
        {
            // Both segments are points.
            s = 0d;
            t = 0d;
        }
        else if (a <= eps)
        {
            // Segment A is a point.
            s = 0d;
            t = Clamp01(f / e);
        }
        else
        {
            var c = d1.Dot(r);
            if (e <= eps)
            {
                // Segment B is a point.
                t = 0d;
                s = Clamp01(-c / a);
            }
            else
            {
                var b = d1.Dot(d2);
                var denom = (a * e) - (b * b);
                s = denom > eps ? Clamp01(((b * f) - (c * e)) / denom) : 0d;
                t = ((b * s) + f) / e;

                if (t < 0d)
                {
                    t = 0d;
                    s = Clamp01(-c / a);
                }
                else if (t > 1d)
                {
                    t = 1d;
                    s = Clamp01((b - c) / a);
                }
            }
        }

        var closestA = a0 + (d1 * s);
        var closestB = b0 + (d2 * t);
        var distance = (closestA - closestB).Length;
        var midpoint = (closestA + closestB) * 0.5d;
        return (distance, midpoint);
    }

    // Distance from point `p` to segment [a,b], plus the segment parameter t∈[0,1] of the closest point (used to order
    // multiple monster hits along a projectile's per-tick travel so a piercing shot resolves nearest-first).
    public static (double Distance, double T) PointSegmentDistance(WorldVector p, WorldVector a, WorldVector b)
    {
        var ab = b - a;
        var lengthSq = ab.LengthSquared;
        var t = lengthSq > 1e-12d ? Clamp01((p - a).Dot(ab) / lengthSq) : 0d;
        var closest = a + (ab * t);
        return ((p - closest).Length, t);
    }

    private static double Clamp01(double value) => value < 0d ? 0d : (value > 1d ? 1d : value);
}
