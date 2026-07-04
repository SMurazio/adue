namespace Mmo.Shared.Domain.Population;

// PROCEDURAL-POPULATION P1 (docs/procedural-population-design.md D3): a generalized weighted rejection
// scatter -- the SAME proven shape as Zone.PlanResourceNodeScatter (SplitMix64 rejection sampling,
// min-spacing via a used-tile set, a bounded attempt budget) extracted and made data-driven so L1 client
// decor, L2 server resource nodes, and (later) ecology spawn-tile derivation (D5) can all call ONE shared
// engine with different seeds/classes instead of three separately-maintained copies of the same sampler.
// Deliberately generic: this file knows nothing about grass/roads/resource nodes -- callers supply
// isCandidate (walkable + surface-category filter) and density (base x distanceCurve x noise, D2) as
// delegates, and get back a deterministic tile list.
//
// This does NOT modify or replace Zone.PlanResourceNodeScatter -- that stays exactly as it is today
// (uniform, density-less) until a later task (P3) migrates it onto this engine.
public static class WeightedScatter
{
    /// <summary>
    /// Scatters up to <paramref name="targetCount"/> tiles across a width x height grid.
    ///
    /// Algorithm per attempt: draw a uniformly-random tile in [0, width) x [0, height). Reject it (no
    /// further PRNG draw consumed) if it is already claimed or fails <paramref name="isCandidate"/>.
    /// Otherwise draw ONE MORE uniform [0, 1) value and accept the tile only if that draw is LESS than
    /// <paramref name="density"/>(tile) -- i.e. a tile with density 0.1 is picked roughly 1 attempt in 10
    /// among candidates, which is exactly the "accept a candidate tile with probability density(tile)"
    /// rule from design D3. Finally reject if the tile is within <paramref name="minSpacing"/> (Chebyshev
    /// distance, matching Zone's existing spacing rule) of an already-PLACED tile, so results still
    /// spread out even where density is uniformly high. The density roll is drawn UNCONDITIONALLY once a
    /// tile passes isCandidate (even for density exactly 0 or 1) so the PRNG sequence a later attempt
    /// sees never depends on how many prior candidates were density- vs spacing-rejected -- the output
    /// stays a pure function of (seed, width, height, isCandidate, density, minSpacing) as the
    /// determinism contract requires.
    ///
    /// Stops at <paramref name="targetCount"/> placements or the attempt budget, whichever comes first --
    /// an unreachable target (too dense for the candidate area, or a density field that is mostly
    /// near-zero) terminates with a partial, fully deterministic set rather than looping forever.
    /// </summary>
    /// <param name="width">Grid width in tiles.</param>
    /// <param name="height">Grid height in tiles.</param>
    /// <param name="seed">
    /// Caller-owned PRNG seed. Callers placing MULTIPLE independent classes from the same zone seed
    /// should salt it per class first (e.g. <c>zoneSeed ^ 0x5C4A11ED</c>, the same discipline
    /// Zone.PlanResourceNodeScatter already uses for its own salt) so different classes don't share an
    /// identical draw sequence.
    /// </param>
    /// <param name="isCandidate">
    /// Absolute filter: a tile the predicate rejects can NEVER be placed on, regardless of density (e.g.
    /// walkability, surface-category restriction, "not already occupied by authored content").
    /// </param>
    /// <param name="density">
    /// Accept-probability in [0, 1] for a tile that already passed <paramref name="isCandidate"/> --
    /// typically base(category) x distanceCurve(distance-to-road) x patchNoise(seed, tile) per design D2.
    /// Values outside [0, 1] are NOT clamped here (a caller bug that produces e.g. 1.3 just means "always
    /// accepted", not a corrupted state) -- clamp upstream if your composition can overshoot.
    /// </param>
    /// <param name="targetCount">Stop once this many tiles are placed. &lt;= 0 returns an empty list.</param>
    /// <param name="minSpacing">
    /// Minimum Chebyshev distance between any two placed tiles. Must be &gt;= 1 (a spacing of 0 would
    /// allow two placements to alias the same tile, which the used-tile set already forbids outright).
    /// </param>
    /// <param name="preclaimed">
    /// Optional tiles to pre-seed the used-set with (e.g. authored marker tiles, like Zone's existing
    /// marker preseed) so the scatter can never stack a placement onto them. Does NOT count toward
    /// <paramref name="targetCount"/> and is never itself returned.
    /// </param>
    /// <param name="maxAttempts">
    /// Override the attempt budget. Defaults to <c>targetCount * 64 + 1024</c>, the same generous-
    /// multiple-plus-floor shape Zone.PlanResourceNodeScatter uses.
    /// </param>
    public static IReadOnlyList<TileCoord> Scatter(
        int width,
        int height,
        int seed,
        Func<TileCoord, bool> isCandidate,
        Func<TileCoord, double> density,
        int targetCount,
        int minSpacing,
        IReadOnlyCollection<TileCoord>? preclaimed = null,
        long? maxAttempts = null)
    {
        if (width < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Width must be positive.");
        }

        if (height < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(height), "Height must be positive.");
        }

        if (minSpacing < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(minSpacing), "minSpacing must be >= 1.");
        }

        ArgumentNullException.ThrowIfNull(isCandidate);
        ArgumentNullException.ThrowIfNull(density);

        var placements = new List<TileCoord>();
        if (targetCount <= 0)
        {
            return placements;
        }

        var used = new HashSet<TileCoord>();
        if (preclaimed is not null)
        {
            foreach (var tile in preclaimed)
            {
                used.Add(tile);
            }
        }

        var budget = maxAttempts ?? ((targetCount * 64L) + 1024L);
        var state = SplitMix64.SeedState(seed);
        var spacingIndex = new SpacingIndex(minSpacing);

        for (var attempt = 0L; attempt < budget && placements.Count < targetCount; attempt++)
        {
            var x = SplitMix64.NextInt(ref state, width);
            var y = SplitMix64.NextInt(ref state, height);
            var tile = new TileCoord(x, y);

            if (used.Contains(tile) || !isCandidate(tile))
            {
                continue;
            }

            // Density acceptance roll -- see the method doc for why this is drawn unconditionally.
            var roll = SplitMix64.NextDouble(ref state);
            if (roll >= density(tile))
            {
                continue;
            }

            if (!spacingIndex.IsFarEnough(tile))
            {
                continue;
            }

            used.Add(tile);
            placements.Add(tile);
            spacingIndex.Add(tile);
        }

        return placements;
    }

    // P2-FLAGGED PERF FIX: the original IsFarEnough linear-scanned the whole placement list per candidate,
    // making Scatter O(targetCount^2) -- at P2's decor scale (12k placements in one call, 27k across a zone
    // build) that is hundreds of millions of Chebyshev tests on the client login path. This spatial hash
    // buckets placed tiles into a grid of cell size = minSpacing; because any tile within Chebyshev
    // distance < minSpacing of a candidate differs by < minSpacing in BOTH axes, it can only live in the
    // candidate's own bucket or one of its 8 neighbors -- so the check inspects at most 9 small buckets.
    // EQUIVALENCE ARGUMENT (the correctness contract): this changes ONLY the spacing lookup's data
    // structure. The PRNG draw sequence, iteration order, and every accept/reject decision are byte-
    // identical to the linear version, so the output remains the same pure function of the inputs -- all
    // pre-existing determinism/distribution tests pass unchanged, and a brute-force-reference equivalence
    // test pins it.
    private sealed class SpacingIndex
    {
        private readonly int _minSpacing;
        private readonly Dictionary<long, List<TileCoord>> _buckets = new();

        public SpacingIndex(int minSpacing)
        {
            _minSpacing = minSpacing;
        }

        public bool IsFarEnough(TileCoord candidate)
        {
            var bx = Bucket(candidate.X);
            var by = Bucket(candidate.Y);
            for (var nx = bx - 1; nx <= bx + 1; nx++)
            {
                for (var ny = by - 1; ny <= by + 1; ny++)
                {
                    if (!_buckets.TryGetValue(Key(nx, ny), out var bucket))
                    {
                        continue;
                    }

                    foreach (var tile in bucket)
                    {
                        var dx = Math.Abs(tile.X - candidate.X);
                        var dy = Math.Abs(tile.Y - candidate.Y);
                        if (Math.Max(dx, dy) < _minSpacing)
                        {
                            return false;
                        }
                    }
                }
            }

            return true;
        }

        public void Add(TileCoord tile)
        {
            var key = Key(Bucket(tile.X), Bucket(tile.Y));
            if (!_buckets.TryGetValue(key, out var bucket))
            {
                bucket = new List<TileCoord>(4);
                _buckets[key] = bucket;
            }

            bucket.Add(tile);
        }

        // Grid coords are non-negative (draws come from [0, width) x [0, height)), but floor-divide safely
        // anyway so a future caller with offset coordinates cannot silently corrupt bucketing.
        private int Bucket(int value) => (int)Math.Floor(value / (double)_minSpacing);

        private static long Key(int bx, int by) => ((long)bx << 32) ^ (uint)by;
    }
}
