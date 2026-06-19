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
        Inventory? inventory = null,
        ResourceNode? resource = null)
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
        Resource = resource;
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

    // Per-entity movement-speed stat. 1.0 (the default) means "move at the server's base step cadence",
    // identical to pre-S51 behaviour. >1 = faster (shorter effective cooldown), <1 = slower. The
    // effective per-step cooldown is derived from this in EffectiveStepCooldownTicks/Ms and clamped, so a
    // silly multiplier can never break the tick loop. Speed buffs/slows/mounts later just set this.
    public double SpeedMultiplier { get; private set; } = 1.0;

    // Sets the speed multiplier, guarding against non-positive / non-finite values (which would otherwise
    // produce a zero or NaN cooldown). Returns true if the value actually changed, so the caller only
    // re-replicates the cadence when it really moved.
    public bool TrySetSpeedMultiplier(double multiplier)
    {
        if (!double.IsFinite(multiplier) || multiplier <= 0)
        {
            return false;
        }

        if (multiplier == SpeedMultiplier)
        {
            return false;
        }

        SpeedMultiplier = multiplier;
        return true;
    }

    // Derives this entity's effective per-step cooldown in TICKS from the server's base cooldown and the
    // speed multiplier, clamped to the configured [minTicks, maxTicks] tick bounds (mirrors the ms clamp).
    // Default multiplier 1.0 returns baseStepCooldownTicks unchanged ⇒ behaviour parity with pre-S51. The
    // clamp guarantees the tick loop always advances (>= 1 tick) regardless of how extreme the multiplier is.
    public uint EffectiveStepCooldownTicks(uint baseStepCooldownTicks, uint minTicks, uint maxTicks)
    {
        var scaled = baseStepCooldownTicks / SpeedMultiplier;
        // Round to the nearest tick; never below 1 (a zero cooldown would let an entity step every tick and
        // is meaningless on a tick-quantised loop).
        var ticks = (long)Math.Max(1, Math.Round(scaled, MidpointRounding.AwayFromZero));
        return (uint)Math.Clamp(ticks, (long)minTicks, (long)maxTicks);
    }

    // Durable per-character inventory (server-memory truth, write-behind persisted). Present only on
    // durable player entities; null for transient/world entities.
    public Inventory? Inventory { get; }

    // Transient resource-node state (available/depleted + respawn timer). Present only on
    // EntityKind.Resource entities that are harvestable; null for players and the legacy placeholder.
    public ResourceNode? Resource { get; }

    // Replicated availability bit that rides EntityStateSnapshot. False (the default) for everything
    // that is not a harvestable resource node, so the snapshot path stays uniform.
    public bool IsDepleted => Resource is { IsAvailable: false };

    // The tick this node is scheduled to respawn at. 0 for non-resource entities. Lets the server queue
    // a depleted node by its respawn time rather than rescanning all nodes each tick.
    public uint ResourceRespawnAtTick => Resource?.RespawnAtTick ?? 0;

    public uint StateRevision { get; private set; } = 1;

    // Harvests the node: marks it depleted, schedules respawn, and bumps StateRevision so the change
    // re-replicates through the AOI snapshot delta path. Caller must have validated availability.
    public void DepleteResource(uint serverTick)
    {
        if (Resource is null)
        {
            return;
        }

        Resource.Deplete(serverTick);
        StateRevision++;
    }

    // Returns this node to Available if its respawn time has arrived, bumping StateRevision so the
    // refreshed availability re-replicates. No-op (returns false) for non-resource or still-depleted
    // entities.
    public bool TryRespawnResource(uint serverTick)
    {
        if (Resource is null || !Resource.TryRespawn(serverTick))
        {
            return false;
        }

        StateRevision++;
        return true;
    }

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
