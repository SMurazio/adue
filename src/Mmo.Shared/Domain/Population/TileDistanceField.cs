namespace Mmo.Shared.Domain.Population;

// PROCEDURAL-POPULATION P1 (docs/procedural-population-design.md D2 "distanceCurve"): a multi-source BFS
// integer distance transform over a full width x height grid. For every tile it holds the 4-neighbor
// tile-count distance to the NEAREST seed tile (e.g. every road/cobble tile) -- computed ONCE at boot per
// zone and then read cheaply per placement query. This is the shared field consumers turn into a
// "civilization suppresses wilderness" curve (D2): decor/nodes thin near roads, thicken far away.
//
// DECISION (flagged as a required call in the P1 task -- documented here per that instruction): distance
// is PURE GRID GEOMETRY. It ignores walkability/blocked status entirely -- a wall tile gets a real
// distance value like any other tile, and blocked tiles never act as obstacles that lengthen a path
// around them (this is a plain multi-source BFS over ALL tiles, not a walkability-aware flood fill). This
// is the simplest of the two options the design doc calls out, and it is the right one for a smooth
// density-falloff field: the field only ever feeds a placement PROBABILITY that is separately gated by
// walkability (WeightedScatter's isCandidate predicate already excludes blocked tiles from ever being
// placed on), so a blocked tile's distance value is simply never read for placement purposes. Treating
// walls as obstacles would make a road "bleed" distance-1 into an adjacent building interior through
// nothing but topology, which is not what "distance to road" should mean for a decor curve. A future
// walkability-aware transform (e.g. for something LOS-like) would be a new method, not a change to this
// one -- see AGENT-FORK note in the P1 review request for why this wasn't built speculatively.
//
// Allocation-sane at 384x384 (147,456 tiles): one int[] the size of the grid, one Queue<TileCoord> that
// holds at most the grid's tile count at once. No recursion (a recursive flood fill on a 384x384 grid
// would blow the stack), no per-tile allocation.
public sealed class TileDistanceField
{
    private readonly int[] _distances; // row-major: index = y * Width + x, matching AuthoredMap's layout.

    private TileDistanceField(int width, int height, int[] distances)
    {
        Width = width;
        Height = height;
        _distances = distances;
    }

    public int Width { get; }

    public int Height { get; }

    public int DistanceAt(TileCoord tile)
    {
        return DistanceAt(tile.X, tile.Y);
    }

    public int DistanceAt(int x, int y)
    {
        if (x < 0 || x >= Width || y < 0 || y >= Height)
        {
            throw new ArgumentOutOfRangeException(nameof(x), $"Tile ({x}, {y}) is outside the {Width}x{Height} distance field.");
        }

        return _distances[(y * Width) + x];
    }

    /// <summary>
    /// Computes the distance field for a width x height grid from the given seed tiles (distance 0 at
    /// every seed, +1 per 4-neighbor BFS layer outward). Seeds outside the grid are silently ignored (a
    /// caller may hand this a raw category scan without pre-filtering bounds). If <paramref name="seeds"/>
    /// is empty, every tile is set to <see cref="int.MaxValue"/> rather than left undefined, so callers
    /// doing arithmetic on the result (e.g. a distanceCurve(d) falloff) never need a separate "no seeds"
    /// special case -- an all-max-value field just means "infinitely far from civilization everywhere",
    /// which is the semantically correct answer for a map with no roads.
    /// </summary>
    public static TileDistanceField Compute(int width, int height, IEnumerable<TileCoord> seeds)
    {
        if (width < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Width must be positive.");
        }

        if (height < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(height), "Height must be positive.");
        }

        ArgumentNullException.ThrowIfNull(seeds);

        var distances = new int[width * height];
        Array.Fill(distances, -1); // -1 = unvisited sentinel, normalized away below before returning.

        var queue = new Queue<TileCoord>();
        foreach (var seed in seeds)
        {
            if (seed.X < 0 || seed.X >= width || seed.Y < 0 || seed.Y >= height)
            {
                continue; // Out-of-bounds seeds are ignored, not an error -- see summary above.
            }

            var index = (seed.Y * width) + seed.X;
            if (distances[index] != -1)
            {
                continue; // Duplicate seed tile -- already enqueued at distance 0.
            }

            distances[index] = 0;
            queue.Enqueue(seed);
        }

        while (queue.Count > 0)
        {
            var tile = queue.Dequeue();
            var nextDistance = distances[(tile.Y * width) + tile.X] + 1;
            TryVisit(tile.Offset(1, 0), nextDistance, width, height, distances, queue);
            TryVisit(tile.Offset(-1, 0), nextDistance, width, height, distances, queue);
            TryVisit(tile.Offset(0, 1), nextDistance, width, height, distances, queue);
            TryVisit(tile.Offset(0, -1), nextDistance, width, height, distances, queue);
        }

        // Normalize any tile the BFS never reached (only possible with zero seeds, since the grid itself
        // is always fully 4-connected) to int.MaxValue instead of leaving the -1 sentinel visible.
        for (var i = 0; i < distances.Length; i++)
        {
            if (distances[i] == -1)
            {
                distances[i] = int.MaxValue;
            }
        }

        return new TileDistanceField(width, height, distances);
    }

    private static void TryVisit(TileCoord tile, int candidateDistance, int width, int height, int[] distances, Queue<TileCoord> queue)
    {
        if (tile.X < 0 || tile.X >= width || tile.Y < 0 || tile.Y >= height)
        {
            return;
        }

        var index = (tile.Y * width) + tile.X;
        if (distances[index] != -1)
        {
            return; // Already visited (BFS guarantees this is the shortest distance already).
        }

        distances[index] = candidateDistance;
        queue.Enqueue(tile);
    }
}
