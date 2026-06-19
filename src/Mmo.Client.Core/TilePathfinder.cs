using System.Collections.Generic;
using Mmo.Shared.Domain;

namespace Mmo.Client.Core;

// 8-way A* over the locally-regenerated blocked map (S42). The client owns the full wall layout, so it
// can plan a route on its own and feed the existing held-direction MoveIntent — the server still
// validates every step and stays authoritative. Walkability mirrors TileGrid.IsWalkable exactly: a tile
// is walkable iff it is in bounds and not blocked. The server applies no diagonal corner-cutting rule
// (WorldEntity.TryStep only checks the destination tile), so neither do we — a diagonal step is valid as
// long as its destination tile is walkable, even if a wall clips the corner. If the server ever adds a
// corner rule, mirror it here.
//
// Cost model is uniform per move (orthogonal and diagonal both cost 1) because the server's step cooldown
// is a flat per-step cadence regardless of direction; minimizing step count is what actually minimizes
// travel time. The heuristic is the Chebyshev distance (admissible under unit step cost), so A* stays
// optimal in step count while still preferring straight-looking diagonals.
public sealed class TilePathfinder
{
    private readonly int _width;
    private readonly int _height;
    private readonly IReadOnlySet<TileCoord> _blocked;

    public TilePathfinder(int width, int height, IReadOnlySet<TileCoord> blockedTiles)
    {
        _width = width;
        _height = height;
        _blocked = blockedTiles ?? throw new System.ArgumentNullException(nameof(blockedTiles));
    }

    public static TilePathfinder FromZone(ZoneModel zone)
    {
        System.ArgumentNullException.ThrowIfNull(zone);
        return new TilePathfinder(zone.Width, zone.Height, zone.BlockedTiles);
    }

    public bool IsWalkable(TileCoord tile)
    {
        return tile.X >= 0 && tile.X < _width
            && tile.Y >= 0 && tile.Y < _height
            && !_blocked.Contains(tile);
    }

    // Returns the route from (exclusive) start to (inclusive) goal as the ordered list of tiles to step
    // onto. Empty when start == goal (nothing to do), when the goal is unwalkable, or when no route
    // exists. The returned list never includes the start tile, so each element is one tile-step from the
    // previous (or from start for the first element).
    public IReadOnlyList<TileCoord> FindPath(TileCoord start, TileCoord goal)
    {
        if (start == goal || !IsWalkable(goal) || !IsWalkable(start))
        {
            return System.Array.Empty<TileCoord>();
        }

        var open = new PriorityQueue<TileCoord, int>();
        var cameFrom = new Dictionary<TileCoord, TileCoord>();
        var gScore = new Dictionary<TileCoord, int> { [start] = 0 };

        open.Enqueue(start, Heuristic(start, goal));

        while (open.TryDequeue(out var current, out _))
        {
            if (current == goal)
            {
                return Reconstruct(cameFrom, current);
            }

            var currentG = gScore[current];
            for (var i = 0; i < Neighbours.Length; i++)
            {
                var neighbour = current.Offset(Neighbours[i].X, Neighbours[i].Y);
                if (!IsWalkable(neighbour))
                {
                    continue;
                }

                var tentativeG = currentG + 1;
                if (gScore.TryGetValue(neighbour, out var existing) && tentativeG >= existing)
                {
                    continue;
                }

                cameFrom[neighbour] = current;
                gScore[neighbour] = tentativeG;
                open.Enqueue(neighbour, tentativeG + Heuristic(neighbour, goal));
            }
        }

        return System.Array.Empty<TileCoord>();
    }

    private static IReadOnlyList<TileCoord> Reconstruct(Dictionary<TileCoord, TileCoord> cameFrom, TileCoord goal)
    {
        var path = new List<TileCoord>();
        var current = goal;
        while (cameFrom.TryGetValue(current, out var previous))
        {
            path.Add(current);
            current = previous;
        }

        path.Reverse();
        return path;
    }

    // Chebyshev distance: the minimum number of 8-way unit steps between two tiles. Admissible (never
    // overestimates) under the uniform unit step cost, so A* returns a shortest (fewest-step) path.
    private static int Heuristic(TileCoord a, TileCoord b)
    {
        var dx = System.Math.Abs(a.X - b.X);
        var dy = System.Math.Abs(a.Y - b.Y);
        return System.Math.Max(dx, dy);
    }

    // The 8 neighbour offsets, in Direction8 order so iteration is deterministic and matches the movement
    // model's direction set exactly.
    private static readonly TileCoord[] Neighbours =
    [
        Direction8.N.Delta(),
        Direction8.NE.Delta(),
        Direction8.E.Delta(),
        Direction8.SE.Delta(),
        Direction8.S.Delta(),
        Direction8.SW.Delta(),
        Direction8.W.Delta(),
        Direction8.NW.Delta(),
    ];
}
