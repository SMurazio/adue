using Mmo.Shared.Domain;

namespace Mmo.Server.Runtime;

public sealed class WorldState
{
    // Default spatial-index cell size (tiles). A pure performance knob — correctness is independent of it
    // (see SpatialEntityGrid). Sized so that for the default 40-tile interest radius a viewer query
    // touches a small fixed neighborhood. GameServer constructs the zone with a cell size derived from
    // the configured interest radius; this default only applies to the few direct `new WorldState()`
    // constructions (tests).
    public const int DefaultGridCellSize = 32;

    private readonly Dictionary<ulong, WorldEntity> _entities = [];
    // MONSTER-AI-DORMANCY (todo/monster-ai-dormancy.md, ecology-v1-design.md §8 E0): a MONSTER-ONLY index, kept in
    // sync at the single Insert/Remove funnel EVERY entity add/removal already goes through (AddPlayer/AddTransient
    // -> Insert; Despawn/logout -> Remove), so it can never desync regardless of which kind of
    // monster-lifecycle call site GameServer adds later. Replaces the O(all-entities) scan-then-filter both
    // GameServer.StepMonsterAi and CopyMonstersTo used to do — the per-tick monster pass is now O(monster count),
    // not O(every entity in the zone including every player/resource/corpse).
    private readonly Dictionary<ulong, WorldEntity> _monsters = [];
    private readonly SpatialEntityGrid _grid;
    private ulong _nextEntityId = 1;

    public WorldState()
        : this(DefaultGridCellSize)
    {
    }

    public WorldState(int gridCellSize)
    {
        _grid = new SpatialEntityGrid(gridCellSize);
    }

    public IReadOnlyCollection<WorldEntity> Entities => _entities.Values;

    // MONSTER-AI-DORMANCY: the monster-only index (see the field doc above). Iteration order matches Dictionary's
    // usual insertion-stable-until-remove order — not depended on by any caller, same as `Entities`.
    public IReadOnlyCollection<WorldEntity> Monsters => _monsters.Values;

    public int Count => _entities.Count;

    public int GridCellSize => _grid.CellSize;

    public void CopyEntitiesTo(ICollection<WorldEntity> destination)
    {
        foreach (var entity in _entities.Values)
        {
            destination.Add(entity);
        }
    }

    // MONSTER-SEPARATION: gather the live MONSTER participants for the per-tick separation pass into a reused buffer
    // (struct-enumerator over _monsters.Values → no boxing/alloc, unlike a foreach over the IReadOnlyCollection).
    // MONSTER-AI-DORMANCY: now sourced from the monster-only index (was an O(all-entities) scan-then-filter) — same
    // resulting set, cheaper to produce as the zone fills with players/resources/corpses alongside monsters.
    // PLAYER SEAM: this is the participant gather — to let players collide with monsters later, also include
    // EntityKind.Player here AND widen MonsterSeparation's candidate filter to match (the two must agree).
    public void CopyMonstersTo(ICollection<WorldEntity> destination)
    {
        foreach (var entity in _monsters.Values)
        {
            destination.Add(entity);
        }
    }

    // Gathers AOI candidates for a viewer centered at `center`: every entity in the spatial cells
    // overlapping the [center ± radiusTiles] box, appended to `destination` (cleared first). This is a
    // SUPERSET of the in-interest set — the caller applies the exact interest test to each candidate, so
    // the final result is identical to a full scan. `radiusTiles` MUST cover the interest exit radius
    // (interest radius + hysteresis), so a hysteresis-retained entity at the box edge is never dropped.
    public void GatherInterestCandidates(TileCoord center, int radiusTiles, List<WorldEntity> destination)
    {
        destination.Clear();
        _grid.QueryNeighborhood(center, radiusTiles, destination);
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

        Insert(entity);
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

        Insert(entity);
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

        _grid.Remove(entity);
        // MONSTER-AI-DORMANCY: keep the monster-only index in sync at this single funnel. A no-op Remove call for a
        // non-monster id (the overwhelmingly common case) costs one dictionary lookup that misses.
        if (entity.Kind == EntityKind.Monster)
        {
            _monsters.Remove(entityId);
        }

        return true;
    }

    // Keeps the spatial index in sync after a movement step changed an entity's tile. Called by the move
    // path with the tile the entity occupied before the step (its Tile property already holds the new
    // tile). A same-cell move is a no-op inside the grid, so most single tile steps cost only an equality
    // check.
    public void OnEntityMoved(WorldEntity entity, TileCoord previousTile)
    {
        _grid.Move(entity, previousTile);
    }

    private void Insert(WorldEntity entity)
    {
        _entities.Add(entity.Id, entity);
        _grid.Add(entity);
        // MONSTER-AI-DORMANCY: mirror into the monster-only index at the single insertion funnel (AddPlayer/
        // AddTransient both route through here) — every EntityKind.Monster ever created is caught,
        // including any future monster-producing call site, with no separate bookkeeping for the caller to remember.
        if (entity.Kind == EntityKind.Monster)
        {
            _monsters.Add(entity.Id, entity);
        }
    }
}
