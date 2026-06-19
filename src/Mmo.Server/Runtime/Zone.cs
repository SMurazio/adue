using Mmo.Shared.Domain;
using Mmo.Server.Configuration;

namespace Mmo.Server.Runtime;

public sealed class Zone
{
    public const string DefaultId = "sandbox";
    public static TileCoord DefaultSpawnTile { get; } = TileGrid.DefaultSpawnTile;

    private readonly TileGrid _tileGrid;
    private readonly TileCoord[] _spawnTiles;
    private int _nextSpawnTileIndex;

    public Zone(string id, TileGrid tileGrid, IEnumerable<TileCoord> spawnTiles)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("Zone id is required.", nameof(id));
        }

        Id = id;
        _tileGrid = tileGrid ?? throw new ArgumentNullException(nameof(tileGrid));
        _spawnTiles = spawnTiles
            .Where(_tileGrid.IsWalkable)
            .Distinct()
            .ToArray();

        if (_spawnTiles.Length == 0)
        {
            throw new ArgumentException("At least one walkable spawn tile is required.", nameof(spawnTiles));
        }
    }

    public string Id { get; }
    public int Width => _tileGrid.Width;
    public int Height => _tileGrid.Height;
    public IReadOnlySet<TileCoord> BlockedTiles => _tileGrid.BlockedTiles;
    public IReadOnlyList<TileCoord> SpawnTiles => _spawnTiles;
    public WorldState World { get; } = new();

    public static Zone CreateDefault(int width, int height, SpawnDistribution spawnDistribution = SpawnDistribution.Distributed)
    {
        var tileGrid = TileGrid.CreateDefault(width, height);
        return new Zone(DefaultId, tileGrid, CreateSpawnTiles(tileGrid, spawnDistribution));
    }

    public bool IsWalkable(TileCoord tile)
    {
        return _tileGrid.IsWalkable(tile);
    }

    public TileCoord ResolveSpawnTile(TileCoord preferredTile)
    {
        if (IsWalkable(preferredTile))
        {
            return preferredTile;
        }

        return _spawnTiles[0];
    }

    public TileCoord ResolvePlayerSpawnTile(TileCoord persistedTile)
    {
        if (IsWalkable(persistedTile) && persistedTile != TileGrid.DefaultSpawnTile)
        {
            return persistedTile;
        }

        return NextSpawnTile();
    }

    public TileCoord NextSpawnTile()
    {
        var index = _nextSpawnTileIndex++ % _spawnTiles.Length;
        return _spawnTiles[index];
    }

    public bool TryStep(WorldEntity entity, Direction8 direction, uint serverTick, uint stepCooldownTicks)
    {
        return entity.TryStep(direction, serverTick, stepCooldownTicks, _tileGrid);
    }

    public bool TryStep(
        WorldEntity entity,
        Direction8 direction,
        uint serverTick,
        uint stepCooldownTicks,
        out MovementStepResult result)
    {
        return entity.TryStep(direction, serverTick, stepCooldownTicks, _tileGrid, out result);
    }

    public WorldEntity SpawnPlayer(
        uint networkId,
        Guid characterId,
        string displayName,
        TileCoord tile,
        ClientSession ownerSession,
        Inventory inventory)
    {
        return World.AddPlayer(networkId, characterId, displayName, ResolveSpawnTile(tile), ownerSession, inventory);
    }

    public WorldEntity SpawnTransient(
        uint networkId,
        EntityKind kind,
        string displayName,
        TileCoord tile,
        Direction8 facing)
    {
        if (!IsWalkable(tile))
        {
            throw new ArgumentException($"Transient spawn tile {tile} is not walkable.", nameof(tile));
        }

        return World.AddTransient(networkId, kind, displayName, tile, facing);
    }

    public WorldEntity SpawnResourceNode(
        uint networkId,
        ResourceNodeDefinition definition,
        TileCoord tile)
    {
        if (!IsWalkable(tile))
        {
            throw new ArgumentException($"Resource node tile {tile} is not walkable.", nameof(tile));
        }

        return World.AddResourceNode(networkId, definition.DisplayName, tile, new ResourceNode(definition));
    }

    // Scatters one node of each registered type onto walkable tiles in a small ring around the first
    // spawn tile. Deliberately tiny: enough to exercise the gather loop near where players spawn. Full
    // map population is a later concern (terrain streaming, S36). Returns the placed tiles in order so
    // callers can rent network ids and wire spawns. Skips any candidate tile that is blocked or already
    // taken so two nodes never share a tile.
    public IReadOnlyList<(ResourceNodeDefinition Definition, TileCoord Tile)> PlanResourceNodeScatter(
        ResourceNodeRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        var origin = _spawnTiles[0];
        // A handful of small offsets around spawn; each registered node type takes the next free one.
        // (2, 0) is deliberately omitted because the legacy placeholder marker sits there.
        var candidates = new[]
        {
            origin.Offset(0, 2),
            origin.Offset(-2, 0),
            origin.Offset(0, -2),
            origin.Offset(2, 2),
            origin.Offset(-2, -2),
            origin.Offset(2, -2),
            origin.Offset(-2, 2),
            origin.Offset(0, 3),
        };

        var placements = new List<(ResourceNodeDefinition, TileCoord)>();
        var used = new HashSet<TileCoord>();
        var candidateIndex = 0;
        foreach (var definition in registry.Definitions)
        {
            while (candidateIndex < candidates.Length)
            {
                var tile = candidates[candidateIndex++];
                if (IsWalkable(tile) && used.Add(tile))
                {
                    placements.Add((definition, tile));
                    break;
                }
            }
        }

        return placements;
    }

    public bool Despawn(ulong entityId, out WorldEntity entity)
    {
        return World.Remove(entityId, out entity);
    }

    private static IReadOnlyList<TileCoord> CreateSpawnTiles(TileGrid tileGrid, SpawnDistribution spawnDistribution)
    {
        var center = new TileCoord(tileGrid.Width / 2, tileGrid.Height / 2);
        if (spawnDistribution == SpawnDistribution.Clustered)
        {
            return [center];
        }

        if (spawnDistribution == SpawnDistribution.Scattered)
        {
            return CreateScatteredSpawnTiles(tileGrid, center);
        }

        var spawnTiles = new List<TileCoord>();
        const int spreadTiles = 32;
        const int spacingTiles = 4;
        for (var y = center.Y - spreadTiles; y <= center.Y + spreadTiles; y += spacingTiles)
        {
            for (var x = center.X - spreadTiles; x <= center.X + spreadTiles; x += spacingTiles)
            {
                var tile = new TileCoord(x, y);
                if (tileGrid.IsWalkable(tile))
                {
                    spawnTiles.Add(tile);
                }
            }
        }

        if (spawnTiles.Count == 0)
        {
            spawnTiles.Add(center);
        }

        return spawnTiles;
    }

    // Distributes spawn tiles in an even grid spanning the whole walkable map, so clients spread
    // across the world (and AOI actually filters) instead of crowding the fixed central patch that
    // Distributed seeds. Spacing scales with map size so the spawn count stays roughly constant
    // (~16 points per axis) regardless of how large the world is. Blocked tiles are skipped.
    private static IReadOnlyList<TileCoord> CreateScatteredSpawnTiles(TileGrid tileGrid, TileCoord center)
    {
        // The outermost ring (x/y == 0 or width/height - 1) is always blocked border, so inset by 1.
        const int margin = 1;
        const int targetPointsPerAxis = 16;

        var minX = margin;
        var maxX = tileGrid.Width - 1 - margin;
        var minY = margin;
        var maxY = tileGrid.Height - 1 - margin;

        var spacingX = Math.Max(1, (maxX - minX) / targetPointsPerAxis);
        var spacingY = Math.Max(1, (maxY - minY) / targetPointsPerAxis);

        var spawnTiles = new List<TileCoord>();
        for (var y = minY; y <= maxY; y += spacingY)
        {
            for (var x = minX; x <= maxX; x += spacingX)
            {
                var tile = new TileCoord(x, y);
                if (tileGrid.IsWalkable(tile))
                {
                    spawnTiles.Add(tile);
                }
            }
        }

        if (spawnTiles.Count == 0)
        {
            spawnTiles.Add(center);
        }

        return spawnTiles;
    }
}
