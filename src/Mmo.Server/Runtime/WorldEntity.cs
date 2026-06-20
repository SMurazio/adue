using Mmo.Shared.Domain;

namespace Mmo.Server.Runtime;

public sealed class WorldEntity
{
    // The earliest server tick at which this entity's next movement action (step OR turn) may fire. Null =
    // never acted, so the first action is always eligible. An ACCEPTED step sets it to serverTick + the full
    // step cooldown; a TURN (S63) sets it to serverTick + the (much smaller) turn delay, so whipping the
    // facing rotates quickly instead of paying a whole step cooldown, while a turn still costs a beat (it is
    // never instant — that would let rapid direction changes move the entity). This single field replaces the
    // old _lastStepTick gate: storing the next-eligible tick directly (rather than backdating _lastStepTick by
    // cooldown - turnDelay) is underflow-safe near tick 0 and keeps the predictor mirror trivial.
    private uint? _nextEligibleTick;

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

    public bool TryStep(Direction8 direction, uint serverTick, uint stepCooldownTicks, uint turnDelayTicks, TileGrid grid)
    {
        return TryStep(direction, serverTick, stepCooldownTicks, turnDelayTicks, grid, out _);
    }

    public bool TryStep(
        Direction8 direction,
        uint serverTick,
        uint stepCooldownTicks,
        uint turnDelayTicks,
        TileGrid grid,
        out MovementStepResult result)
    {
        // Gate on the next-eligible tick (set by the previous step/turn). A step that arrives early — before
        // its cooldown OR a turn's shorter turn-delay has elapsed — is dropped, unchanged from the pre-S63
        // cooldown behaviour for accepted steps.
        if (_nextEligibleTick.HasValue && serverTick < _nextEligibleTick.Value)
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

        // Turn-then-move (UO, S59) + turn delay (S63): a step in a direction we don't already face just TURNS
        // to face it — costs only the turn delay (set below) and re-replicates Facing (StateRevision bump),
        // but does NOT move the tile. Only a step in the current facing direction actually moves (below).
        // Turning is always allowed (you may face a wall). This makes rapid direction changes a clean pivot
        // instead of a zigzag, and the small turn delay keeps a whip rotating in place without moving.
        if (direction != Facing)
        {
            Facing = direction;
            // S63: a turn costs only the (small) turn delay, NOT a full step cooldown. The next step/turn is
            // eligible after turnDelayTicks, so settling on a direction steps at the normal cadence but a whip
            // rotates quickly. Still a beat (turnDelayTicks is clamped >= 0; 0 would make turns instant, which
            // the tuning clamp/quantisation guards against — default 80 ms quantises to >= 1 tick).
            _nextEligibleTick = serverTick + turnDelayTicks;
            StateRevision++;
            result = new MovementStepResult(
                direction,
                Tile,
                Tile,
                CooldownElapsed: true,
                TargetWalkable: false,
                Accepted: false,
                "turn",
                Tile,
                Turned: true);
            return false;
        }

        var delta = direction.Delta();
        var target = Tile.Offset(delta.X, delta.Y);
        // S75: reject diagonal corner-cutting. A diagonal step (both axes non-zero) also slices between the two
        // orthogonally-adjacent tiles it passes; if EITHER of those side tiles is blocked, the move would slip
        // diagonally THROUGH a wall corner. So a diagonal is walkable only when the destination AND both side
        // tiles ((Tile.X+dx, Tile.Y) and (Tile.X, Tile.Y+dy)) are walkable. Cardinal steps (one axis zero) are
        // unchanged: only the destination matters. The client predictor (LocalPlayerPredictor.Tick) applies the
        // IDENTICAL rule via its walkability oracle so prediction still mirrors the server exactly.
        if (!IsStepWalkable(delta, target, grid))
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
        _nextEligibleTick = serverTick + stepCooldownTicks;
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

    // S75: walkability of a step from the current tile, with diagonal corner-cutting rejected. The destination
    // must always be walkable. For a DIAGONAL step (both delta axes non-zero) the two orthogonally-adjacent
    // tiles it cuts between must ALSO be walkable, so the entity can't slip diagonally through a wall corner.
    // Cardinal steps (one axis zero) check the destination only. The client predictor mirrors this rule exactly
    // (LocalPlayerPredictor) so server and prediction reject the identical set of diagonal steps.
    private bool IsStepWalkable(TileCoord delta, TileCoord target, TileGrid grid)
    {
        if (!grid.IsWalkable(target))
        {
            return false;
        }

        if (delta.X != 0 && delta.Y != 0)
        {
            return grid.IsWalkable(Tile.Offset(delta.X, 0)) && grid.IsWalkable(Tile.Offset(0, delta.Y));
        }

        return true;
    }
}
