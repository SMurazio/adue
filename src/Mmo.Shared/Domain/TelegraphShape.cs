namespace Mmo.Shared.Domain;

// TELEGRAPH T1 (docs/ability-telegraph-sync-design.md): the ground-telegraph SHAPE — the area a scheduled ability
// resolves against at its resolve tick. Three kinds ship: CIRCLE (origin + radius), WEDGE (a pie-slice from the origin
// apex — reach radius + half-angle + aim bearing), and LINE (an oriented rectangle — length along the aim bearing from
// the origin + half-width across). The Kind discriminator is the seam content extends WITHOUT reshaping the schedule
// entry or the wire event: a new kind is a new enum member + its params + a Contains/BoundingRadius case + a codec case.
// SHARED (not server-only) because T2 replicates exactly this shape to AOI viewers for the client fill rendering, and —
// the load-bearing invariant — server RESOLVE and the client DECAL are the SAME shape from the SAME wire fields, so what
// is rendered IS the hit test (the fair-and-responsive pillar).
public enum TelegraphShapeKind : byte
{
    Circle = 1,
    // WEDGE + LINE (S-telegraph-shapes-wedge-line): the Sunderer's Cleave (130° wedge) and Lunge (2u-wide line). Both
    // reuse the Origin + Radius envelope (Radius = wedge reach / line length) and append their extra params on the wire.
    Wedge = 2,
    Line = 3,
}

// The shape value: which Kind + its params. A record struct (cheap, copyable, value-equal) captured ONCE at schedule
// time — the origin/aim are LOCKED at cast (that is what makes a telegraph dodgeable); only the MEMBERSHIP test runs at
// the resolve tick, against live continuous positions.
//
// The field triad after Origin is REUSED across kinds so the 3-arg positional ctor `new(Kind, Origin, Radius)` still
// builds a circle (the extra params default 0) and the wire/scheduler keep one regular envelope:
//   * Radius           — circle radius / wedge reach / line LENGTH (the along-bearing extent).
//   * AimRadians       — the aim BEARING (wedge + line), atan2(dz,dx): +X east, +Z south (the AimAngle convention).
//   * HalfAngleRadians — the wedge half-angle (total cleave arc = 2·this).
//   * HalfWidth        — the line half-width (total corridor width = 2·this).
public readonly record struct TelegraphShape(
    TelegraphShapeKind Kind,
    WorldVector Origin,
    double Radius,
    double AimRadians = 0d,
    double HalfAngleRadians = 0d,
    double HalfWidth = 0d)
{
    public static TelegraphShape Circle(WorldVector origin, double radius) =>
        new(TelegraphShapeKind.Circle, origin, radius);

    // WEDGE: a pie-slice with its APEX at `origin`, reaching `radius` within ±`halfAngleRadians` of `aimRadians`.
    public static TelegraphShape Wedge(WorldVector origin, double radius, double aimRadians, double halfAngleRadians) =>
        new(TelegraphShapeKind.Wedge, origin, radius, aimRadians, halfAngleRadians);

    // LINE: an oriented rectangle with one short EDGE at `origin`, extending `length` along `aimRadians` and
    // ±`halfWidth` across it.
    public static TelegraphShape Line(WorldVector origin, double length, double aimRadians, double halfWidth) =>
        new(TelegraphShapeKind.Line, origin, length, aimRadians, 0d, halfWidth);

    // True iff a continuous position is inside the shape. MEMBERSHIP IS CENTER-POINT — DECIDED (user, 2026-07-03): you
    // are hit iff your character's CENTER is inside the drawn shape. A body CLIPPING the edge (center outside, body
    // radius overlapping) NEVER counts — the fair-and-responsive pillar: the drawn shape IS the rule, and ambiguity errs
    // player-favorable (being hit while your center looks out of the zone is exactly the unfairness we refuse). This is
    // DELIBERATELY divergent from the melee/free-aim body-clip hit tests (FreeAimSector widens by the body's angular
    // half-width; a telegraph does NOT) and stays so: a telegraph is a dodge-the-zone rule (legibility first). Corollary
    // for renderers (T2): draw the TRUE shape — no visual padding/shrink (the honest-telegraph rule). An unknown kind
    // contains nothing (safe default: a malformed shape can never damage anyone).
    //
    //   * Circle — Euclidean distance <= Radius (INCLUSIVE rim), compared squared to skip the sqrt.
    //   * Wedge  — within the reach radius AND the bearing to the point is within ±HalfAngleRadians of AimRadians. The
    //              apex itself (distance 0, no defined bearing) is INSIDE. Edge inclusive both ways.
    //   * Line   — the along-bearing projection lies in [0, Length] AND the perpendicular distance is <= HalfWidth. Edge
    //              inclusive.
    public bool Contains(WorldVector position) => Kind switch
    {
        TelegraphShapeKind.Circle => (position - Origin).LengthSquared <= Radius * Radius,
        TelegraphShapeKind.Wedge => WedgeContains(position),
        TelegraphShapeKind.Line => LineContains(position),
        _ => false,
    };

    private bool WedgeContains(WorldVector position)
    {
        var d = position - Origin;
        var distSquared = d.LengthSquared;
        if (distSquared > Radius * Radius)
        {
            return false;
        }

        if (distSquared <= 0d)
        {
            return true; // the apex: no defined bearing, but it is the wedge's own origin — inside.
        }

        var bearing = System.Math.Atan2(d.Y, d.X);
        var delta = NormalizePi(bearing - AimRadians);
        return System.Math.Abs(delta) <= HalfAngleRadians;
    }

    private bool LineContains(WorldVector position)
    {
        var d = position - Origin;
        var ux = System.Math.Cos(AimRadians);
        var uy = System.Math.Sin(AimRadians);

        // Along-bearing projection must lie within the segment [0, Length]; perpendicular distance (|d × u|, u unit) must
        // be within the half-width. Both inclusive (edge-of-the-drawn-rectangle counts, like the circle rim).
        var along = (d.X * ux) + (d.Y * uy);
        if (along < 0d || along > Radius)
        {
            return false;
        }

        var perp = System.Math.Abs((d.X * uy) - (d.Y * ux));
        return perp <= HalfWidth;
    }

    // Reduce an angle difference to the principal range (-π, π] so the |delta| <= halfAngle test is correct across the
    // 0/2π seam (same helper FreeAimSector uses — the wedge shares the free-aim bearing convention).
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

    // The radius of a disc (around Origin) guaranteed to contain the whole shape — the SUPERSET bound a spatial gather
    // queries before the exact Contains test (the AOI gather pattern). Circle/Wedge: the reach Radius. Line: the far
    // corner from the origin edge, sqrt(Length² + HalfWidth²). Kind-independent CALLERS (the resolve engine never
    // switches on Kind) — the switch lives here.
    public double BoundingRadius => Kind == TelegraphShapeKind.Line
        ? System.Math.Sqrt((Radius * Radius) + (HalfWidth * HalfWidth))
        : Radius;
}
