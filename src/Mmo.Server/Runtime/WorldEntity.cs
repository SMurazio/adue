using Mmo.Shared.Domain;

namespace Mmo.Server.Runtime;

public sealed class WorldEntity
{
    private uint? _lastStepTick;

    public WorldEntity(
        ulong id,
        uint networkId,
        EntityKind kind,
        TileCoord tile,
        Direction8 facing,
        string displayName,
        Guid? characterId,
        ClientSession? ownerSession,
        bool isDurable,
        Inventory? inventory = null)
    {
        Id = id;
        NetworkId = networkId;
        Kind = kind;
        Tile = tile;
        Facing = facing;
        DisplayName = displayName;
        CharacterId = characterId;
        OwnerSession = ownerSession;
        IsDurable = isDurable;
        Inventory = inventory;
    }

    public ulong Id { get; }
    public uint NetworkId { get; }
    public EntityKind Kind { get; }
    public TileCoord Tile { get; private set; }
    public Direction8 Facing { get; private set; }
    public string DisplayName { get; }
    public Guid? CharacterId { get; }
    public ClientSession? OwnerSession { get; }
    public bool IsDurable { get; }

    // Durable per-character inventory (server-memory truth, write-behind persisted). Present only on
    // durable player entities; null for transient/world entities.
    public Inventory? Inventory { get; }

    public uint StateRevision { get; private set; } = 1;

    public bool TryStep(Direction8 direction, uint serverTick, uint stepCooldownTicks, TileGrid grid)
    {
        return TryStep(direction, serverTick, stepCooldownTicks, grid, out _);
    }

    public bool TryStep(
        Direction8 direction,
        uint serverTick,
        uint stepCooldownTicks,
        TileGrid grid,
        out MovementStepResult result)
    {
        if (_lastStepTick.HasValue && serverTick - _lastStepTick.Value < stepCooldownTicks)
        {
            var cooldownDelta = direction.Delta();
            var cooldownTarget = Tile.Offset(cooldownDelta.X, cooldownDelta.Y);
            result = new MovementStepResult(
                direction,
                Tile,
                cooldownTarget,
                CooldownElapsed: false,
                grid.IsWalkable(cooldownTarget),
                Accepted: false,
                "cooldown",
                Tile);
            return false;
        }

        var delta = direction.Delta();
        var target = Tile.Offset(delta.X, delta.Y);
        // TODO: reject diagonal corner-cutting once tiles can carry richer collision flags.
        if (!grid.IsWalkable(target))
        {
            result = new MovementStepResult(
                direction,
                Tile,
                target,
                CooldownElapsed: true,
                TargetWalkable: false,
                Accepted: false,
                grid.IsInBounds(target) ? "blocked" : "out_of_bounds",
                Tile);
            return false;
        }

        var from = Tile;
        Tile = target;
        Facing = direction;
        _lastStepTick = serverTick;
        StateRevision++;
        result = new MovementStepResult(
            direction,
            from,
            target,
            CooldownElapsed: true,
            TargetWalkable: true,
            Accepted: true,
            "accepted",
            Tile);
        return true;
    }
}
