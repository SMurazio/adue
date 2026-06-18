using Mmo.Shared.Domain;

namespace Mmo.Server.Runtime;

public sealed class Zone
{
    public const string DefaultId = "sandbox";
    public static TileCoord DefaultSpawnTile { get; } = TileGrid.DefaultSpawnTile;

    private readonly TileGrid _tileGrid;
    private readonly TileCoord[] _spawnTiles;

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

    public static Zone CreateDefault(int width, int height)
    {
        return new Zone(DefaultId, TileGrid.CreateDefault(width, height), [DefaultSpawnTile]);
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

    public bool TryStep(WorldEntity entity, Direction8 direction, uint serverTick, uint stepCooldownTicks)
    {
        return entity.TryStep(direction, serverTick, stepCooldownTicks, _tileGrid);
    }

    public WorldEntity SpawnPlayer(
        uint networkId,
        Guid characterId,
        string displayName,
        TileCoord tile,
        ClientSession ownerSession)
    {
        return World.AddPlayer(networkId, characterId, displayName, ResolveSpawnTile(tile), ownerSession);
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

    public bool Despawn(ulong entityId, out WorldEntity entity)
    {
        return World.Remove(entityId, out entity);
    }
}
