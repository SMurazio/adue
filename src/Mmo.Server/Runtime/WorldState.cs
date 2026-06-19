using Mmo.Shared.Domain;

namespace Mmo.Server.Runtime;

public sealed class WorldState
{
    private readonly Dictionary<ulong, WorldEntity> _entities = [];
    private ulong _nextEntityId = 1;

    public IReadOnlyCollection<WorldEntity> Entities => _entities.Values;

    public void CopyEntitiesTo(ICollection<WorldEntity> destination)
    {
        foreach (var entity in _entities.Values)
        {
            destination.Add(entity);
        }
    }

    public WorldEntity AddPlayer(
        uint networkId,
        Guid characterId,
        string displayName,
        TileCoord tile,
        ClientSession ownerSession,
        Inventory inventory)
    {
        var entity = new WorldEntity(
            _nextEntityId++,
            networkId,
            EntityKind.Player,
            tile,
            Direction8.S,
            displayName,
            characterId,
            ownerSession,
            isDurable: true,
            inventory);

        _entities.Add(entity.Id, entity);
        return entity;
    }

    public WorldEntity AddTransient(
        uint networkId,
        EntityKind kind,
        string displayName,
        TileCoord tile,
        Direction8 facing)
    {
        var entity = new WorldEntity(
            _nextEntityId++,
            networkId,
            kind,
            tile,
            facing,
            displayName,
            characterId: null,
            ownerSession: null,
            isDurable: false);

        _entities.Add(entity.Id, entity);
        return entity;
    }

    // Server-owned harvestable resource node. Transient (not durable, no owner session) but carries a
    // ResourceNode for its available/depleted state. Spawned at world setup, not derived from sessions.
    public WorldEntity AddResourceNode(
        uint networkId,
        string displayName,
        TileCoord tile,
        ResourceNode resource)
    {
        var entity = new WorldEntity(
            _nextEntityId++,
            networkId,
            EntityKind.Resource,
            tile,
            Direction8.S,
            displayName,
            characterId: null,
            ownerSession: null,
            isDurable: false,
            inventory: null,
            resource: resource);

        _entities.Add(entity.Id, entity);
        return entity;
    }

    public bool TryGet(ulong entityId, out WorldEntity entity)
    {
        return _entities.TryGetValue(entityId, out entity!);
    }

    public bool Remove(ulong entityId, out WorldEntity entity)
    {
        if (!_entities.Remove(entityId, out entity!))
        {
            return false;
        }

        return true;
    }
}
