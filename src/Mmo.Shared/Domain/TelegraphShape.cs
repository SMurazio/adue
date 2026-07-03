namespace Mmo.Shared.Domain;

// TELEGRAPH T1 (docs/ability-telegraph-sync-design.md): the ground-telegraph SHAPE — the area a scheduled ability
// resolves against at its resolve tick. CIRCLE only this phase (origin + radius in world units); the Kind
// discriminator is the seam later content (cone/line) extends WITHOUT reshaping the server's schedule entry or the
// future wire event: a new kind is a new enum member + extra params + a Contains case. SHARED (not server-only)
// because T2 replicates exactly this {kind, origin, radius} to AOI viewers for the client fill rendering.
public enum TelegraphShapeKind : byte
{
    Circle = 1,
}

// The shape value: which Kind + its params. A record struct (cheap, copyable, value-equal) captured ONCE at schedule
// time — the origin is LOCKED at cast (that is what makes a telegraph dodgeable); only the MEMBERSHIP test runs at
// the resolve tick, against live continuous positions.
public readonly record struct TelegraphShape(TelegraphShapeKind Kind, WorldVector Origin, double Radius)
{
    public static TelegraphShape Circle(WorldVector origin, double radius) =>
        new(TelegraphShapeKind.Circle, origin, radius);

    // True iff a continuous position is inside the shape. Circle: Euclidean distance <= Radius (INCLUSIVE — a body
    // centre exactly on the rim is hit), compared squared to skip the sqrt. An unknown kind contains nothing (safe
    // default: a malformed shape can never damage anyone).
    public bool Contains(WorldVector position) => Kind switch
    {
        TelegraphShapeKind.Circle => (position - Origin).LengthSquared <= Radius * Radius,
        _ => false,
    };

    // The radius of a disc (around Origin) guaranteed to contain the whole shape — the SUPERSET bound a spatial
    // gather queries before the exact Contains test (the AOI gather pattern). Kind-independent on purpose so the
    // resolve engine never switches on Kind; a future cone/line computes its own bound here.
    public double BoundingRadius => Radius;
}
