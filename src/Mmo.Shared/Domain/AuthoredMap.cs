namespace Mmo.Shared.Domain;

/// <summary>
/// The kind of prop a marker char pins to a tile in an authored map (town-blockout D3). Byte-backed
/// because the marker kind is hashed into the terrain ContentHash — never reorder/renumber existing
/// values, only append, or every shipped authored map's hash silently moves.
/// </summary>
public enum AuthoredMarkerKind : byte
{
    /// <summary>`H` — a solid house prop (existing casa_magica archetype via DisplayName).</summary>
    House = 0,

    /// <summary>`P` — a portal prop (existing portalemagico archetype; the future floor-2 stub).</summary>
    Portal = 1,

    /// <summary>`T` — pins a specific tree resource node (the town oak), on top of the D6 scatter.</summary>
    TreePin = 2,

    /// <summary>`R` — pins a specific rock resource node (the quarry rock), on top of the D6 scatter.</summary>
    RockPin = 3,
}

/// <summary>A prop marker parsed from an authored map: which prop kind, at which tile.</summary>
public readonly record struct AuthoredMarker(AuthoredMarkerKind Kind, TileCoord Tile);

/// <summary>
/// A hand-authored map parsed from an ASCII grid — ONE string per row, ONE char per tile, row 0 = y 0,
/// char index = x (row-major). The single source of truth for authored zones (town-blockout D1): the
/// same characters produce the blocked set (collision), the per-tile surface category (visuals), the
/// spawn anchors, and the prop markers, so collision and visuals can never disagree.
///
/// The char alphabet is the authoring contract (town-blockout D3):
///   `#` wall (blocked) · `.` grass · `,` dirt/road · `:` town cobble · `-` dungeon stone ·
///   `~` water (blocked + <see cref="SurfaceCategory.Water"/>) · `S` spawn anchor (walkable cobble) ·
///   `H` house / `P` portal / `T` tree-pin / `R` rock-pin (walkable grass + marker) ·
///   space = out-of-world (blocked; row padding for non-rectangular worlds).
/// Any OTHER char is a parse ERROR — authored content fails loudly at parse (boot/test), never
/// silently. Ragged rows (rows of different lengths) are likewise a parse error: pad with spaces.
///
/// Determinism contract (same as <see cref="TerrainGenerator"/>): parsing is pure — identical rows
/// yield identical output on every platform/culture/runtime. All emitted lists (blocked, spawns,
/// markers, out-of-world) are in canonical row-major scan order (y, then x) by construction, so
/// hashing them is stable everywhere.
/// </summary>
public sealed class AuthoredMap
{
    private readonly HashSet<TileCoord> _blockedSet;
    private readonly HashSet<TileCoord> _outOfWorldSet;
    private readonly SurfaceCategory[] _categories; // row-major: index = y * Width + x

    private AuthoredMap(
        int width,
        int height,
        List<TileCoord> blockedTiles,
        SurfaceCategory[] categories,
        List<TileCoord> spawnTiles,
        List<AuthoredMarker> markers,
        List<TileCoord> outOfWorldTiles)
    {
        Width = width;
        Height = height;
        BlockedTiles = blockedTiles;
        _categories = categories;
        SpawnTiles = spawnTiles;
        Markers = markers;
        OutOfWorldTiles = outOfWorldTiles;
        _blockedSet = new HashSet<TileCoord>(blockedTiles);
        _outOfWorldSet = new HashSet<TileCoord>(outOfWorldTiles);
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Blocked tiles in canonical row-major order — walls, water, and out-of-world padding.</summary>
    public IReadOnlyList<TileCoord> BlockedTiles { get; }

    /// <summary>`S` spawn anchor tiles (walkable cobble), in row-major order. Empty when none authored.</summary>
    public IReadOnlyList<TileCoord> SpawnTiles { get; }

    /// <summary>Prop markers (`H`/`P`/`T`/`R`), in row-major order. The marker tile itself is walkable grass.</summary>
    public IReadOnlyList<AuthoredMarker> Markers { get; }

    /// <summary>
    /// Space (out-of-world padding) tiles, row-major. A SUBSET of <see cref="BlockedTiles"/> kept
    /// separately so the client painter can distinguish "wall standing in the world" (draw a box)
    /// from "nothing exists here" (draw nothing) — both are equally blocked for movement.
    /// </summary>
    public IReadOnlyList<TileCoord> OutOfWorldTiles { get; }

    /// <summary>Walkable tile count — the flood-fill target for the no-orphan-pockets invariant.</summary>
    public int WalkableTileCount => (Width * Height) - BlockedTiles.Count;

    public bool IsInBounds(TileCoord tile)
    {
        return tile.X >= 0 && tile.X < Width && tile.Y >= 0 && tile.Y < Height;
    }

    public bool IsBlocked(TileCoord tile)
    {
        return _blockedSet.Contains(tile);
    }

    public bool IsWalkable(TileCoord tile)
    {
        return IsInBounds(tile) && !_blockedSet.Contains(tile);
    }

    public bool IsOutOfWorld(TileCoord tile)
    {
        return _outOfWorldSet.Contains(tile);
    }

    public SurfaceCategory CategoryAt(TileCoord tile)
    {
        return CategoryAt(tile.X, tile.Y);
    }

    public SurfaceCategory CategoryAt(int x, int y)
    {
        if (x < 0 || x >= Width || y < 0 || y >= Height)
        {
            throw new ArgumentOutOfRangeException(
                nameof(x), $"Tile ({x}, {y}) is outside the {Width}x{Height} authored map.");
        }

        return _categories[(y * Width) + x];
    }

    /// <summary>
    /// Parses an ASCII grid into an authored map. Fails loudly (never silently) on an unknown char,
    /// ragged rows, or an empty grid — authored content errors must surface at boot/test time.
    /// </summary>
    public static AuthoredMap Parse(string[] rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Length == 0)
        {
            throw new ArgumentException("Authored map has no rows.", nameof(rows));
        }

        var width = rows[0]?.Length
            ?? throw new ArgumentException("Authored map row 0 is null.", nameof(rows));
        if (width == 0)
        {
            throw new ArgumentException("Authored map rows are empty strings.", nameof(rows));
        }

        var height = rows.Length;
        var blocked = new List<TileCoord>();
        var categories = new SurfaceCategory[width * height];
        var spawns = new List<TileCoord>();
        var markers = new List<AuthoredMarker>();
        var outOfWorld = new List<TileCoord>();

        for (var y = 0; y < height; y++)
        {
            var row = rows[y]
                ?? throw new ArgumentException($"Authored map row {y} is null.", nameof(rows));
            if (row.Length != width)
            {
                throw new ArgumentException(
                    $"Authored map rows are ragged: row {y} has {row.Length} chars, row 0 has {width}. " +
                    "Every row must be the same length (pad out-of-world with spaces).",
                    nameof(rows));
            }

            for (var x = 0; x < width; x++)
            {
                var tile = new TileCoord(x, y);
                // The D3 alphabet. Emission order is the row-major scan itself, so every output list
                // is canonically ordered by construction (no defensive sort needed — asserted by test).
                switch (row[x])
                {
                    case '#': // Wall: blocked; category is the Grass default (never painted — walls draw as boxes).
                        blocked.Add(tile);
                        break;
                    case ' ': // Out-of-world padding: blocked AND flagged so the painter can draw nothing at all.
                        blocked.Add(tile);
                        outOfWorld.Add(tile);
                        break;
                    case '~': // Water: blocked (no swim tech) but a real painted surface (blue visual anchor).
                        blocked.Add(tile);
                        categories[(y * width) + x] = SurfaceCategory.Water;
                        break;
                    case '.':
                        categories[(y * width) + x] = SurfaceCategory.Grass;
                        break;
                    case ',':
                        categories[(y * width) + x] = SurfaceCategory.Dirt;
                        break;
                    case ':':
                        categories[(y * width) + x] = SurfaceCategory.Cobble;
                        break;
                    case '-':
                        categories[(y * width) + x] = SurfaceCategory.DungeonStone;
                        break;
                    case 'S': // Spawn anchor: walkable town cobble (D4 — new players wake in the plaza).
                        categories[(y * width) + x] = SurfaceCategory.Cobble;
                        spawns.Add(tile);
                        break;
                    case 'H':
                        categories[(y * width) + x] = SurfaceCategory.Grass;
                        markers.Add(new AuthoredMarker(AuthoredMarkerKind.House, tile));
                        break;
                    case 'P':
                        categories[(y * width) + x] = SurfaceCategory.Grass;
                        markers.Add(new AuthoredMarker(AuthoredMarkerKind.Portal, tile));
                        break;
                    case 'T':
                        categories[(y * width) + x] = SurfaceCategory.Grass;
                        markers.Add(new AuthoredMarker(AuthoredMarkerKind.TreePin, tile));
                        break;
                    case 'R':
                        categories[(y * width) + x] = SurfaceCategory.Grass;
                        markers.Add(new AuthoredMarker(AuthoredMarkerKind.RockPin, tile));
                        break;
                    default:
                        throw new ArgumentException(
                            $"Authored map has unknown char '{row[x]}' (U+{(int)row[x]:X4}) at column {x}, row {y}. " +
                            "Allowed: '#' '.' ',' ':' '-' '~' 'S' 'H' 'P' 'T' 'R' and space.",
                            nameof(rows));
                }
            }
        }

        return new AuthoredMap(width, height, blocked, categories, spawns, markers, outOfWorld);
    }

    /// <summary>
    /// Flood-fills the walkable tiles 4-neighbor-connected to <paramref name="start"/> and returns the
    /// reachable set. The reusable core of the NO-ORPHAN-POCKETS invariant (town-blockout §4): from any
    /// spawn anchor, every walkable tile must be reachable — see <see cref="AllWalkableReachableFrom"/>.
    /// 4-neighbor (not 8) because diagonal-only gaps between two blocked tiles are not traversable by
    /// the circle-vs-wall collision resolver anyway — a diagonal squeeze would be a false "reachable".
    /// Throws if the start tile is not walkable (an authored-content error, surfaced loudly).
    /// </summary>
    public IReadOnlySet<TileCoord> FloodFillWalkableFrom(TileCoord start)
    {
        if (!IsWalkable(start))
        {
            throw new ArgumentException(
                $"Flood-fill start {start} is not a walkable tile of the {Width}x{Height} authored map.",
                nameof(start));
        }

        var reached = new HashSet<TileCoord> { start };
        var frontier = new Stack<TileCoord>();
        frontier.Push(start);
        while (frontier.Count > 0)
        {
            var tile = frontier.Pop();
            Consider(tile.Offset(1, 0), reached, frontier);
            Consider(tile.Offset(-1, 0), reached, frontier);
            Consider(tile.Offset(0, 1), reached, frontier);
            Consider(tile.Offset(0, -1), reached, frontier);
        }

        return reached;
    }

    /// <summary>True iff every walkable tile is 4-neighbor reachable from <paramref name="start"/>.</summary>
    public bool AllWalkableReachableFrom(TileCoord start)
    {
        return FloodFillWalkableFrom(start).Count == WalkableTileCount;
    }

    private void Consider(TileCoord tile, HashSet<TileCoord> reached, Stack<TileCoord> frontier)
    {
        if (IsWalkable(tile) && reached.Add(tile))
        {
            frontier.Push(tile);
        }
    }
}
