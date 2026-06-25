namespace Mmo.Shared.Domain;

// Continuous world POSITION/offset in tile units: 1.0 == one tile, a tile-centre is the integer pair (X, Y).
// The migration's leaf position type — entity positions become a WorldVector (Phase 0 keeps them tile-centre
// valued so behaviour is byte-for-byte the tile-stepped model; later phases let them hold fractional values).
//
// WHY double, not float: the proven continuous experiment (ContinuousMover / ContinuousCollision /
// ContinuousPredictor) integrates and reconciles in DOUBLE. Client prediction must reproduce the server's
// integration bit-for-bit (Phase 4 reconcile compares predicted vs authoritative position); float accumulates
// divergent rounding across many sub-steps and would break that determinism. double is the locked choice — do
// NOT "optimize" this to float.
//
// WHY (X, Y), not (X, Z): the game is 2D top-down. The experiment spike used a 3D (X, Z) ground plane; the
// Phase 1 port maps that Z onto Y here so the whole server/codebase stays in a single 2D (X, Y) convention that
// lines up tile-for-tile with TileCoord (X, Y).
public readonly record struct WorldVector(double X, double Y)
{
    public static readonly WorldVector Zero = new(0d, 0d);

    public WorldVector Add(WorldVector other) => new(X + other.X, Y + other.Y);

    public WorldVector Subtract(WorldVector other) => new(X - other.X, Y - other.Y);

    // Scales both components by a scalar (e.g. velocity × dt).
    public WorldVector Scale(double scalar) => new(X * scalar, Y * scalar);

    public static WorldVector operator +(WorldVector a, WorldVector b) => a.Add(b);

    public static WorldVector operator -(WorldVector a, WorldVector b) => a.Subtract(b);

    public static WorldVector operator *(WorldVector v, double scalar) => v.Scale(scalar);

    public static WorldVector operator *(double scalar, WorldVector v) => v.Scale(scalar);

    public double LengthSquared => (X * X) + (Y * Y);

    public double Length => System.Math.Sqrt(LengthSquared);

    public double Dot(WorldVector other) => (X * other.X) + (Y * other.Y);

    // Unit vector in the same direction, or Zero for a zero-length vector (no NaN). Used by the Phase 1+
    // integrator/steering; harmless in Phase 0 (nothing calls it on a non-zero vector yet).
    public WorldVector Normalized()
    {
        var length = Length;
        return length > 0d ? new WorldVector(X / length, Y / length) : Zero;
    }

    // --- Tile/continuous bridges (the cheap boundary) ---

    // The continuous position of a tile's CENTRE. In Phase 0 every entity position is exactly one of these,
    // so the round-trip FromTile(t).ToTileRounded() == t is the identity (asserted in the unit tests).
    public static WorldVector FromTile(TileCoord tile) => new(tile.X, tile.Y);

    public static WorldVector FromTile(int x, int y) => new(x, y);

    // Nearest tile (round to nearest integer on each axis). The grid/walkability boundary: a position at an
    // exact tile centre rounds back to that tile losslessly. Round-away-from-zero so .5 is deterministic.
    public TileCoord ToTileRounded() => new(
        (int)System.Math.Round(X, System.MidpointRounding.AwayFromZero),
        (int)System.Math.Round(Y, System.MidpointRounding.AwayFromZero));

    // Tile this position sits WITHIN (floor on each axis) — the containing grid cell. Distinct from
    // ToTileRounded (nearest centre); kept for the Phase 2 collision/cell-derivation path.
    public TileCoord ToTileFloored() => new(
        (int)System.Math.Floor(X),
        (int)System.Math.Floor(Y));
}
