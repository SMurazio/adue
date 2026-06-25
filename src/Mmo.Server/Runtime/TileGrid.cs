using Mmo.Shared.Domain;

namespace Mmo.Server.Runtime;

public sealed class TileGrid
{
    private readonly HashSet<TileCoord> _blockedTiles;

    public TileGrid(int width, int height, IEnumerable<TileCoord> blockedTiles)
    {
        if (width < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Width must be positive.");
        }

        if (height < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(height), "Height must be positive.");
        }

        Width = width;
        Height = height;
        _blockedTiles = blockedTiles
            .Where(IsInBounds)
            .ToHashSet();
    }

    public static TileCoord DefaultSpawnTile { get; } = new(8, 8);

    public int Width { get; }
    public int Height { get; }
    public IReadOnlySet<TileCoord> BlockedTiles => _blockedTiles;

    // The map is content, not state: the server builds its authoritative TileGrid from the same shared
    // deterministic generator the clients use, so it never has to ship the blocked-tile list. The
    // historical "default" map is genVersion 1 with a fixed default seed (overload below).
    public static TileGrid CreateDefault(int width, int height)
    {
        return CreateGenerated(width, height, DefaultSeed, TerrainGenerator.CurrentGenVersion);
    }

    public static TileGrid CreateGenerated(int width, int height, int seed, int genVersion)
    {
        var blocked = TerrainGenerator.Generate(width, height, seed, genVersion);
        return new TileGrid(width, height, blocked);
    }

    /// <summary>Stable default seed so the generated map (and persisted tile positions) survive restarts.</summary>
    public const int DefaultSeed = 0;

    public bool IsInBounds(TileCoord tile)
    {
        return tile.X >= 0 && tile.X < Width && tile.Y >= 0 && tile.Y < Height;
    }

    public bool IsWalkable(TileCoord tile)
    {
        return IsInBounds(tile) && !_blockedTiles.Contains(tile);
    }

    // CONTINUOUS MIGRATION (Phase 2): the per-tick nearby-walls query for the PLAYER continuous integrator. Given the
    // body's start Position, this tick's `delta` (velocity x dt) and `radius`, compute the swept body AABB
    // (start..end, each expanded by `radius`), floor/ceil it to an INCLUSIVE tile box, and emit one collision Wall per
    // blocked tile inside that box — in STABLE ROW-MAJOR order, into the caller's REUSED scratch buffer (zero per-tick
    // alloc; the single-threaded tick loop owns the buffer). The wall derivation is the SHARED Mmo.Shared TileWalls
    // (the EXACT function the Phase-4 client predictor calls), so the same (blocked set, box) yields the same Wall[]
    // in the same order on both sides — the determinism contract.
    //
    // The box is a deterministic SUPERSET of the swept+radius region: it bounds the resolver's wall set without
    // needing a tighter per-tile test (the resolver itself ignores walls the circle never reaches). At sub-tile
    // per-tick deltas this is ~2x2-3x3 tiles. The box is NOT clamped to grid bounds — a blocked border tile just
    // outside the swept region is simply absent from `_blockedTiles`/out of range of the probe, and the perimeter
    // ring is always blocked anyway; probing a few out-of-bounds coords is a cheap Contains miss and keeps the box
    // math branch-simple (the resolver only ever sees in-set blocked tiles).
    // CONTINUOUS MIGRATION (Phase 4): now a thin FORWARDER to the shared Mmo.Shared.Domain.TileWalls.NeighborhoodWallsForMove,
    // which owns the swept-AABB box-math + wall derivation that formerly lived inline here. The extraction is byte-identical
    // (a server-collision parity test asserts QueryNearbyWalls == NeighborhoodWallsForMove); the point is that the Phase-4
    // client predictor calls the SAME shared helper against ZoneModel.BlockedTiles, so server and prediction derive an
    // identical wall set from the same (blocked, start, delta, radius) — the determinism linchpin.
    public void QueryNearbyWalls(
        WorldVector start,
        WorldVector delta,
        double radius,
        List<ContinuousCollision.Wall> scratch)
    {
        TileWalls.NeighborhoodWallsForMove(_blockedTiles, start, delta, radius, scratch);
    }
}
