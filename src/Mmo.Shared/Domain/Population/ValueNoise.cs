namespace Mmo.Shared.Domain.Population;

// PROCEDURAL-POPULATION P1 (docs/procedural-population-design.md D2 "patchNoise"): seeded lattice value
// noise with bilinear (smoothstep-eased) interpolation. Deliberately NOT Perlin/simplex noise -- the
// design doc calls this out explicitly: a lattice of independently-hashed pseudo-random values at integer
// cell corners, smoothly interpolated between them, reads as thickets-and-clearings instead of uniform
// sprinkle and is indistinguishable from gradient noise at TILE granularity, for a fraction of the code
// and zero external dependency.
//
// Determinism: every lattice corner's value is derived purely from (seed, cellX, cellY) via SplitMix64 --
// no shared mutable state, no sampling-order dependency -- so LatticeValue(seed, 3, 5) is the exact same
// value whenever/however it's asked, the SAME seed always paints the SAME patch pattern, and a different
// seed reshuffles it completely. This matches the PRNG discipline of every other generator in the repo
// (TerrainGenerator, Zone.PlanResourceNodeScatter): no System.Random, no clocks, pure 64-bit arithmetic.
public static class ValueNoise
{
    /// <summary>
    /// Samples the noise field at tile (x, y), returning a value in [0, 1]. <paramref name="cellScale"/>
    /// is the tile span of one lattice cell -- e.g. 8.0 means the noise pattern varies smoothly over
    /// 8-tile blocks (larger = broader patches, smaller = tighter/noisier texture). Must be positive.
    /// </summary>
    public static double Sample(int seed, int x, int y, double cellScale)
    {
        if (cellScale <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(cellScale), "cellScale must be positive.");
        }

        var cellX = x / cellScale;
        var cellY = y / cellScale;
        var x0 = (int)Math.Floor(cellX);
        var y0 = (int)Math.Floor(cellY);
        var x1 = x0 + 1;
        var y1 = y0 + 1;

        // Fractional position within the cell, smoothstep-eased (3t^2 - 2t^3) so interpolation has zero
        // slope at every cell boundary -- avoids a visible crease at lattice lines for a couple extra
        // multiplies, still trivial closed-form math (no lookup tables, no dependency).
        var tx = Smoothstep(cellX - x0);
        var ty = Smoothstep(cellY - y0);

        var v00 = LatticeValue(seed, x0, y0);
        var v10 = LatticeValue(seed, x1, y0);
        var v01 = LatticeValue(seed, x0, y1);
        var v11 = LatticeValue(seed, x1, y1);

        var top = v00 + ((v10 - v00) * tx);
        var bottom = v01 + ((v11 - v01) * tx);
        return top + ((bottom - top) * ty);
    }

    private static double Smoothstep(double t)
    {
        return t * t * (3.0 - (2.0 * t));
    }

    // One deterministic pseudo-random value in [0, 1] per lattice corner. Each corner gets its OWN fresh
    // SplitMix64 stream seeded from (seed, cellX, cellY) folded together -- not an advancing shared
    // state -- so corners can be queried in any order (or repeatedly, e.g. shared between two adjacent
    // cells) and always agree.
    private static double LatticeValue(int seed, int cellX, int cellY)
    {
        var state = SplitMix64.SeedState(CombineCoords(seed, cellX, cellY));
        return SplitMix64.NextDouble(ref state);
    }

    // Folds (seed, cellX, cellY) into one 32-bit key for SplitMix64.SeedState. Population lattice
    // coordinates are small (well under 16 bits even at a 384x384 grid with a cellScale of 1), so the
    // multiply-xor fold below has no practical collisions; correctness never depends on that anyway --
    // this is a hash key feeding a PRNG, not an index, so a rare collision would just mean two corners
    // share a value, not a determinism break.
    private static int CombineCoords(int seed, int cellX, int cellY)
    {
        unchecked
        {
            var h = seed;
            h = (h * 486187739) ^ cellX;
            h = (h * 486187739) ^ cellY;
            return h;
        }
    }
}
