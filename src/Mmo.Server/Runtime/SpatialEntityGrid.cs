using Mmo.Shared.Domain;

namespace Mmo.Server.Runtime;

// Uniform-grid spatial index over world entities, keyed by tile→cell. Lets the AOI candidate gather
// touch only entities in the cells overlapping a viewer's interest box instead of scanning every
// entity (S41). The index is a pure performance accelerator: it never decides visibility itself —
// callers still apply the exact same per-entity interest test to every candidate the grid returns, so
// the result set is identical to a full scan. Correctness therefore does not depend on the cell size
// (a perf knob); it depends only on QueryNeighborhood returning a superset of every entity that could
// pass the interest test for the given query radius.
//
// Not thread-safe: it is mutated and queried solely from the single tick/network-callback thread, the
// same threading model as WorldState's entity table.
internal sealed class SpatialEntityGrid
{
    // Cell coordinate packed into a single long key (two 32-bit cell indices). Cell indices use floored
    // division so negative tile coordinates (defensive — world tiles are non-negative today) still map
    // monotonically.
    private readonly Dictionary<long, List<WorldEntity>> _cells = [];
    private readonly int _cellSize;

    public SpatialEntityGrid(int cellSize)
    {
        if (cellSize < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(cellSize), cellSize, "Cell size must be >= 1.");
        }

        _cellSize = cellSize;
    }

    public int CellSize => _cellSize;

    public void Add(WorldEntity entity)
    {
        var key = CellKey(entity.TileCoord);
        if (!_cells.TryGetValue(key, out var bucket))
        {
            bucket = [];
            _cells[key] = bucket;
        }

        bucket.Add(entity);
    }

    public void Remove(WorldEntity entity)
    {
        RemoveFromCell(CellKey(entity.TileCoord), entity);
    }

    // Migrates an entity that has moved from one tile to another. No-op when the move stayed inside the
    // same cell (the common case for a single tile step), so per-step index maintenance is usually a
    // pure equality check with no bucket churn. Must be called with the tile the entity occupied BEFORE
    // the move (its current Tile is the new one).
    public void Move(WorldEntity entity, TileCoord previousTile)
    {
        var previousKey = CellKey(previousTile);
        var currentKey = CellKey(entity.TileCoord);
        if (previousKey == currentKey)
        {
            return;
        }

        RemoveFromCell(previousKey, entity);
        Add(entity);
    }

    // Appends every entity in the cells overlapping the axis-aligned box [center ± radiusTiles] to the
    // destination. Always a SUPERSET of the entities a Chebyshev/Euclidean interest test of the same
    // radius could accept, because the box of cells fully covers that radius. Callers apply the exact
    // interest test to each appended candidate. Allocation-free apart from growing the caller's list.
    public void QueryNeighborhood(TileCoord center, int radiusTiles, List<WorldEntity> destination)
    {
        var minCellX = CellIndex(center.X - radiusTiles);
        var maxCellX = CellIndex(center.X + radiusTiles);
        var minCellY = CellIndex(center.Y - radiusTiles);
        var maxCellY = CellIndex(center.Y + radiusTiles);

        for (var cellY = minCellY; cellY <= maxCellY; cellY++)
        {
            for (var cellX = minCellX; cellX <= maxCellX; cellX++)
            {
                if (_cells.TryGetValue(PackCell(cellX, cellY), out var bucket))
                {
                    destination.AddRange(bucket);
                }
            }
        }
    }

    private void RemoveFromCell(long key, WorldEntity entity)
    {
        if (!_cells.TryGetValue(key, out var bucket))
        {
            return;
        }

        // Swap-remove: order within a cell does not matter (the caller sorts the final candidate set), so
        // removal stays O(bucket) without shifting. Buckets are small (a cell ≈ the interest box).
        var index = bucket.IndexOf(entity);
        if (index < 0)
        {
            return;
        }

        var last = bucket.Count - 1;
        bucket[index] = bucket[last];
        bucket.RemoveAt(last);
        if (bucket.Count == 0)
        {
            _cells.Remove(key);
        }
    }

    private long CellKey(TileCoord tile)
    {
        return PackCell(CellIndex(tile.X), CellIndex(tile.Y));
    }

    private int CellIndex(int coordinate)
    {
        // Floored division toward negative infinity so cell boundaries are uniform across the origin.
        return (int)Math.Floor(coordinate / (double)_cellSize);
    }

    private static long PackCell(int cellX, int cellY)
    {
        return ((long)cellX << 32) | (uint)cellY;
    }
}
