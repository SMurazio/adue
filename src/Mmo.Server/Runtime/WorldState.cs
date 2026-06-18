using Mmo.Shared.Domain;

namespace Mmo.Server.Runtime;

public sealed class WorldState
{
    private readonly Dictionary<ulong, WorldEntity> _entities = [];
    private ulong _nextEntityId = 1;

    public IReadOnlyCollection<WorldEntity> Entities => _entities.Values;

    public WorldEntity AddPlayer(
        uint networkId,
        Guid characterId,
        string displayName,
        TileCoord tile,
        ClientSession ownerSession)
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
            isDurable: true);

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
